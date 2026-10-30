using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// Companionship features that run beside the original pet: touch, snacks, mood, welcome back,
// good morning / bedtime, health reminders and a focus timer. Everything runs on this computer.
public partial class MainWindow
{
    private DispatcherTimer? careTimer;
    private DateTime careLast = DateTime.UtcNow;
    private CompanionMemoryStore? memory;
    private int pendingReunionDays;

    // presence
    private bool away;
    private DateTime awayStartUtc;
    private bool awayWasFullscreen;
    private double continuousActiveSeconds;
    private DateTime nextLonelyNudge = DateTime.MinValue;
    private DateTime nextBedtimeNudge = DateTime.MinValue;
    private double companionSecondsBuffer;

    // health
    private double sitActiveSeconds, waterActiveSeconds, eyeActiveSeconds;

    // focus
    private DateTime focusUntil = DateTime.MinValue, breakUntil = DateTime.MinValue;
    private bool FocusActive => DateTime.UtcNow < focusUntil;
    private int FocusMinutesLeft => (int)Math.Ceiling(Math.Max(0, (focusUntil - DateTime.UtcNow).TotalMinutes));

    // touch
    private readonly Queue<DateTime> patTurns = new();
    private readonly Queue<DateTime> recentClicks = new();
    private double patLastX = double.NaN, patTravel;
    private int patDirection;
    private DateTime patLastSeen, patCooldown, angryCooldown;

    private Window? petPanel;

    // Features that should not run while she is busy with the owner.
    private bool SpeechPaused => FocusActive || GameActive || chatWindow != null;
    private bool CrawlBlocked => FocusActive || (GameActive && preferences.GameCompanion) || chatWindow != null;
    private bool CanChatter => companionReady && IsVisible && !hiddenByUser && !fullscreenHidden && !PuppetActive && !dragging
        && !exitAnimation && !FocusActive && chatWindow == null && companionSettings == null && modelInstaller == null;

    private void InitializeCare()
    {
        LoadBond();
        memory = CompanionMemoryStore.Load(companionRandom);
        LoadReminders();
        var absent = DateTime.UtcNow - bond.LastSeenUtc;
        if (absent.TotalDays >= 3)
        {
            pendingReunionDays = (int)absent.TotalDays;
            AddAffection(-Math.Min(8, (absent.TotalDays - 3) * .5));
        }
        bond.LastGreetDate = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); // the startup bow greets
        bond.LastSeenUtc = DateTime.UtcNow;
        SaveBondSoon();
        PreviewMouseMove += CompanionPatMove;
        PreviewMouseDown += RaiseOnTouch;
        InitializeOpacity();
        InitializeGameWatch();
        careTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        careTimer.Tick += CareTick;
        careTimer.Start();
    }

    private void ShutdownCare()
    {
        careTimer?.Stop();
        ShutdownGameWatch();
        StopMimic();
        chatWindow?.Close();
        petPanel?.Close();
        SetClickThrough(false);
        SaveBondNow();
        memory?.Save();
        SaveReminders();
    }

    private CompanionAnimationRule? TryFindRule(string id) =>
        animationPolicy is null ? null : animationPolicy.Rules.FirstOrDefault(r => r.Id == id);

    private T Pick<T>(IReadOnlyList<T> options) => options[companionRandom.Next(options.Count)];

    // Drops a model line that is still being generated so it cannot overwrite what she says now.
    private void SupersedeModelSpeech()
    {
        if (!speechRequestPending && !weatherRequestPending) return;
        speechRequestVersion++;
        speechRequestPending = weatherRequestPending = false;
        EndModelSpeech();
    }

    // Shows a line in her bubble (and optionally plays a reaction). Important lines stay until
    // dismissed and also reach the tray when she is hidden.
    private void Say(string text, string? animation = null, bool important = false)
    {
        if (companionClosed || exitAnimation || PuppetActive || !companionReady) return;
        if (important && hiddenByUser) RestoreCompanion();
        if (important) RaiseForNotice();
        if (!IsVisible)
        {
            if (important) notifyIcon?.ShowBalloonTip(10000, "蓝色大肥鱼", text, System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        SupersedeModelSpeech();
        if (animation != null && !dragging && TryFindRule(animation) is { } rule)
        {
            CancelMovement();
            PlayAnimation(rule, "Interact");
        }
        ShowCompanionNotice(text, !important);
        if (important)
        {
            // In a game the bubble cannot be clicked away (clicks pass through), so it closes by itself.
            bubbleUntil = clickThrough ? DateTime.UtcNow.AddSeconds(15) : DateTime.MaxValue;
            var quiet = DateTime.UtcNow.AddMinutes(3); // keep the next automatic line from covering it
            if (nextSpeech < quiet) nextSpeech = quiet;
        }
    }

    private void CareTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var local = DateTime.Now;
        double dt = Math.Clamp((now - careLast).TotalSeconds, 0, 5);
        careLast = now;
        if (companionClosed || exitAnimation || !companionReady) return;
        bond.RollDay(local);
        if (now >= bondSaveDue) SaveBondNow();
        double idle = CompanionNative.IdleSeconds;
        UpdateGame(now);
        ApplyTopmost();
        CheckReminders(now);
        UpdateFocus(now);
        UpdatePresence(now, local, idle, dt);
        UpdateHealth(dt, idle);
    }

    // ---------------------------------------------------------------- presence
    private static readonly string[] MorningLines =
    [
        "{0}早安！今天也要元气满满哦～鱼鱼会一直陪着你的！",
        "早上好呀{0}～昨晚睡得好吗？今天也一起加油吧！",
        "{0}早～新的一天开始啦，先喝杯水再开工吧～"
    ];
    private static readonly string[] BedtimeLines =
    [
        "{0}，已经很晚啦……早点休息好不好？",
        "唔……鱼鱼都困了，{0}也该睡觉觉了吧～",
        "{0}再不睡，明天会没精神的！鱼鱼要生气了哦",
        "都这么晚了……{0}答应鱼鱼，忙完这一点就去睡，好不好？"
    ];
    private static readonly string[] LonelyLines =
    [
        "{0}……鱼鱼一直在旁边哦，要不要摸摸鱼鱼的头？",
        "唔，{0}好久没理鱼鱼了……鱼鱼有点寂寞惹",
        "{0}忙了好久啦，偶尔也看看鱼鱼嘛～"
    ];

    private string Fill(string template) => string.Format(template, bond.Address);

    private void UpdatePresence(DateTime now, DateTime local, double idle, double dt)
    {
        bool active = idle < 60;
        if (active) continuousActiveSeconds += dt;
        else if (idle > 300) continuousActiveSeconds = 0;
        if (active && IsVisible)
        {
            companionSecondsBuffer += dt;
            if (companionSecondsBuffer >= 60)
            {
                companionSecondsBuffer -= 60;
                bond.CompanionMinutes++;
                if ((int)bond.CompanionMinutes % 30 == 0) AddAffection(.5);
                SaveBondSoon();
            }
        }

        if (!away && idle >= 600)
        {
            away = true;
            awayStartUtc = now - TimeSpan.FromSeconds(idle);
            awayWasFullscreen = CompanionNative.IsFullscreen();
            return;
        }
        if (away && idle < 3)
        {
            away = false;
            var gone = now - awayStartUtc;
            sitActiveSeconds = eyeActiveSeconds = 0;
            if (gone.TotalMinutes >= 20) waterActiveSeconds = 0;
            if (preferences.WelcomeBack && !awayWasFullscreen && CanChatter && !GameActive) WelcomeBack(gone, local);
            return;
        }

        if (pendingReunionDays > 0 && animationPhase != "Startup" && CanChatter)
        {
            Say($"{bond.Address}！好久不见……{pendingReunionDays} 天没见到你，鱼鱼好想你呜呜～", "点击回应-开心跃动");
            pendingReunionDays = 0;
            return;
        }

        string today = local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (idle < 5 && local.Hour >= 5 && local.Hour < 11 && bond.LastGreetDate != today && CanChatter)
        {
            bond.LastGreetDate = today;
            SaveBondSoon();
            Say(Fill(Pick(MorningLines)), "超大伸懒腰");
            return;
        }

        bool lateNight = local.Hour == 23 && local.Minute >= 30 || local.Hour < 4;
        if (preferences.BedtimeReminder && lateNight && active && !FocusActive && companionReady && IsVisible && !PuppetActive && chatWindow == null)
        {
            string night = (local.Hour < 12 ? local.AddDays(-1) : local).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (bond.BedtimeDate != night) { bond.BedtimeDate = night; bond.BedtimeCount = 0; nextBedtimeNudge = now.AddMinutes(1); SaveBondSoon(); }
            if (now >= nextBedtimeNudge && bond.BedtimeCount < BedtimeLines.Length)
            {
                string line = Fill(BedtimeLines[bond.BedtimeCount]);
                if (GameActive) line += "游戏明天再玩也来得及～";
                bond.BedtimeCount++;
                nextBedtimeNudge = now.AddMinutes(30);
                SaveBondSoon();
                Say(line, "哈欠连天");
                return;
            }
        }

        if (CanChatter && !GameActive && continuousActiveSeconds > 1800 && now >= nextLonelyNudge
            && (now - bond.LastInteractionUtc).TotalHours >= 3 && bubble.Visibility != Visibility.Visible)
        {
            nextLonelyNudge = now.AddHours(3);
            Say(Fill(Pick(LonelyLines)), "东张西望");
        }
    }

    private void WelcomeBack(TimeSpan gone, DateTime local)
    {
        string duration = gone.TotalMinutes < 60 ? $"{(int)gone.TotalMinutes} 分钟"
            : gone.TotalHours < 24 ? $"{gone.TotalHours:0.#} 个小时" : "好久好久";
        string line;
        if (NearMeal(local) && gone.TotalMinutes is >= 20 and <= 150)
            line = $"{bond.Address}回来啦！是去吃饭了吗？吃饱饱了没～";
        else if (gone.TotalMinutes < 30)
            line = $"{bond.Address}回来啦～刚刚去了 {duration}，鱼鱼乖乖等着呢！";
        else if (gone.TotalHours < 3)
            line = $"{bond.Address}终于回来啦！{duration}没见，鱼鱼好想你～";
        else
            line = $"{bond.Address}！你去了好久好久……鱼鱼一直在等你哦";
        AddAffection(.5);
        NoteInteraction();
        Say(line, "点击回应-元气挥手");
    }

    private string FarewellLine()
    {
        var hour = DateTime.Now.Hour;
        if (hour >= 21 || hour < 4) return $"{bond.Address}晚安～鱼鱼也要睡觉觉啦，明天见！";
        return FarewellText;
    }

    // ---------------------------------------------------------------- health reminders
    private void UpdateHealth(double dt, double idle)
    {
        bool active = idle < 60;
        if (idle >= 300 && !CompanionNative.IsFullscreen()) { sitActiveSeconds = 0; eyeActiveSeconds = 0; }
        if (!active) return;
        sitActiveSeconds += dt; waterActiveSeconds += dt; eyeActiveSeconds += dt;
        if (FocusActive || PuppetActive || dragging || exitAnimation || chatWindow != null || !IsVisible) return; // wait, keep counting
        bool sit = preferences.SitReminder && sitActiveSeconds >= preferences.SitMinutes * 60;
        bool water = preferences.WaterReminder && waterActiveSeconds >= preferences.WaterMinutes * 60;
        bool eye = preferences.EyeReminder && eyeActiveSeconds >= preferences.EyeMinutes * 60;
        if (!sit && !water && !eye) return;
        var parts = new List<string>();
        string? animation = null;
        if (sit) { parts.Add($"已经连续坐了 {preferences.SitMinutes} 分钟啦，站起来伸伸懒腰、走两步吧"); animation ??= "超大伸懒腰"; sitActiveSeconds = 0; }
        if (eye) { parts.Add("看看远处二十秒，让眼睛也休息一下"); animation ??= "东张西望"; eyeActiveSeconds = 0; }
        if (water) { parts.Add("记得喝口水哦，鱼鱼也要咕噜咕噜补充水分"); animation ??= "鲸鱼吐泡泡特效"; waterActiveSeconds = 0; }
        string prefix = GameActive ? $"{bond.Address}，游戏间隙打断一下下～" : $"{bond.Address}，";
        RaiseForNotice();
        Say(prefix + string.Join("；还有，", parts) + "～", GameActive ? null : animation);
    }

    // ---------------------------------------------------------------- focus timer
    private void ToggleFocus()
    {
        if (FocusActive) { StopFocus(false); return; }
        StartFocus();
    }

    private void StartFocus()
    {
        if (!companionReady || exitAnimation || PuppetActive) return;
        focusUntil = DateTime.UtcNow.AddMinutes(preferences.FocusMinutes);
        breakUntil = DateTime.MinValue;
        if (crawl is { Finished: false } walking) walking.StopRequested = true; // come down and sit still
        if (hiddenByUser) RestoreCompanion();
        Say($"好！鱼鱼陪{bond.Address}专注 {preferences.FocusMinutes} 分钟，不吵你～加油！", "工作状态-忙碌点按");
    }

    private void StopFocus(bool completed)
    {
        focusUntil = DateTime.MinValue;
        if (completed)
        {
            bond.Focus++;
            AddAffection(2);
            NoteInteraction();
            sitActiveSeconds = eyeActiveSeconds = 0;
            breakUntil = DateTime.UtcNow.AddMinutes(preferences.BreakMinutes);
            Say($"叮！专注完成～{bond.Address}超棒的！休息 {preferences.BreakMinutes} 分钟吧，起来走走、喝口水～", "工作状态-雀跃庆祝", true);
        }
        else Say("好哒，专注先到这里～辛苦啦！", "点击回应-元气挥手");
    }

    private void UpdateFocus(DateTime now)
    {
        if (focusUntil != DateTime.MinValue && now >= focusUntil) StopFocus(true);
        else if (breakUntil != DateTime.MinValue && now >= breakUntil)
        {
            breakUntil = DateTime.MinValue;
            Say("休息时间结束啦～想继续的话，右键鱼鱼点「专注」就好！", "点击回应-元气挥手");
        }
    }

    // AnimationBoundary hook: during focus she only breathes quietly (or scribbles notes).
    private bool FocusBoundary()
    {
        if (!FocusActive || exitAnimation || dragging || crawl != null) return false;
        var rule = TryFindRule(companionRandom.NextDouble() < .15 ? "轻快记录" : "待机呼吸休闲");
        if (rule == null) return false;
        PlayAnimation(rule, "Idle");
        return true;
    }

    // ---------------------------------------------------------------- touch
    private static readonly string[] PatLinesShy = ["诶？被、被摸头了……有点不好意思", "唔……主人的手好暖", "突然摸头……鱼鱼会害羞的啦"];
    private static readonly string[] PatLinesWarm = ["嘿嘿，好舒服～", "主人的手好暖和～鱼鱼最喜欢了", "再摸一下下嘛～", "呼噜呼噜……鱼鱼要变成小猫了"];
    private static readonly string[] PatLinesLove = ["最喜欢主人摸头了！再多摸一会儿嘛～", "嘿嘿……被主人宠着的感觉真好", "主人摸头的时候，鱼鱼的尾巴都在摇哦～"];
    private static readonly string[] AngryLines = ["呜……不要一直戳鱼鱼啦！", "哼！再戳鱼鱼就不理你了！", "戳戳戳……鱼鱼的脸都要被戳扁了！"];
    private static readonly string[] ClickLinesLow = ["诶？主人叫我吗？", "唔？", "鱼鱼在呢～"];
    private static readonly string[] ClickLinesHigh = ["嘿嘿，主人又来找鱼鱼玩啦～", "主人主人，鱼鱼在这儿！", "被点到了！要奖励鱼鱼大白饭哦～"];

    private void ResetPat() { patTurns.Clear(); patLastX = double.NaN; patDirection = 0; patTravel = 0; }

    // Moving the cursor back and forth over her head (without pressing) counts as a head pat.
    private void CompanionPatMove(object sender, MouseEventArgs e)
    {
        if (dragging || exitAnimation || !companionReady || PuppetActive || videoImage == null
            || e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed) return;
        var p = e.GetPosition(videoImage);
        double fx = p.X * 640 / Math.Max(1, videoImage.ActualWidth), fy = p.Y * 360 / Math.Max(1, videoImage.ActualHeight);
        var now = DateTime.UtcNow;
        bool onHead = fx >= 230 && fx <= 410 && fy >= 48 && fy <= 170;
        if (!onHead) { if ((now - patLastSeen).TotalSeconds > .6) ResetPat(); return; }
        patLastSeen = now;
        if (double.IsNaN(patLastX)) { patLastX = fx; return; }
        double dx = fx - patLastX;
        patLastX = fx;
        if (Math.Abs(dx) < .5) return;
        int direction = Math.Sign(dx);
        if (patDirection == 0 || direction == patDirection) { patDirection = direction; patTravel += Math.Abs(dx); return; }
        if (patTravel >= 12) patTurns.Enqueue(now);
        patDirection = direction;
        patTravel = Math.Abs(dx);
        while (patTurns.Count > 0 && (now - patTurns.Peek()).TotalSeconds > 1.6) patTurns.Dequeue();
        if (patTurns.Count >= 3 && now >= patCooldown)
        {
            ResetPat();
            patCooldown = now.AddSeconds(8);
            OnPat();
        }
    }

    private void OnPat()
    {
        bond.RollDay(DateTime.Now);
        bond.Pats++;
        NoteInteraction();
        AddAffection(bond.Pats <= 10 ? 1.5 : .3);
        SetMood("开心", TimeSpan.FromMinutes(10));
        var lines = bond.Affection < 25 ? PatLinesShy : bond.Affection < 65 ? PatLinesWarm : PatLinesLove;
        Say(Pick(lines), companionRandom.NextDouble() < .5 ? "点击回应-挠痒咯咯笑" : "点击回应-开心跃动");
    }

    // Click hook: poking her many times makes her grumpy; otherwise she sometimes says something.
    private bool PokeReaction()
    {
        var now = DateTime.UtcNow;
        NoteInteraction();
        recentClicks.Enqueue(now);
        while (recentClicks.Count > 0 && (now - recentClicks.Peek()).TotalSeconds > 5) recentClicks.Dequeue();
        if (recentClicks.Count < 4 || now < angryCooldown) return false;
        recentClicks.Clear();
        angryCooldown = now.AddSeconds(60);
        bond.RollDay(DateTime.Now);
        bond.Pokes++;
        AddAffection(-.5);
        SetMood("生气", TimeSpan.FromMinutes(3));
        var rule = TryFindRule("点击回应-傲娇生气");
        if (rule == null) return false;
        PlayAnimation(rule, "Click");
        SupersedeModelSpeech();
        ShowCompanionNotice(Pick(AngryLines), true);
        return true;
    }

    private void ClickChatter()
    {
        if (bubble.Visibility == Visibility.Visible || companionRandom.NextDouble() > .2) return;
        AddAffection(.1);
        SupersedeModelSpeech();
        ShowCompanionNotice(Pick(bond.Affection < 40 ? ClickLinesLow : ClickLinesHigh), true);
    }

    // ---------------------------------------------------------------- snacks
    private static readonly (string Name, string Animation, double Affection, string[] Lines)[] Snacks =
    [
        ("大白饭", "吃白饭", 3, new[] { "哇！是香喷喷的大白饭！鱼鱼最最最喜欢了！", "大白饭！主人最懂鱼鱼了～开动啦！" }),
        ("小鱼干", "大口吃零食", 2, new[] { "小鱼干！咔嚓咔嚓，好香～", "嘿嘿，谢谢主人的小鱼干～" }),
        ("冰淇淋", "吃冰淇淋融化", 2, new[] { "冰淇淋！凉凉甜甜的……啊要化了要化了！", "哇～冰淇淋！主人也来一口嘛" }),
        ("糖葫芦", "吃糖葫芦", 2, new[] { "糖葫芦！酸酸甜甜的，鱼鱼好开心～", "一颗、两颗……嘿嘿，舍不得吃完" }),
        ("西瓜", "吃西瓜", 2, new[] { "大西瓜！咔嚓～好甜好甜！", "西瓜籽要吐掉哦……噗～" }),
        ("饺子", "吃饺子", 2, new[] { "饺子！热乎乎的，主人真好～", "一口一个，鱼鱼吃得好幸福" }),
    ];

    private void Feed(int index)
    {
        if (!companionReady || exitAnimation || PuppetActive || index < 0 || index >= Snacks.Length) return;
        bond.RollDay(DateTime.Now);
        var snack = Snacks[index];
        if (bond.Feeds >= 6)
        {
            Say("唔……鱼鱼已经吃得圆滚滚的啦，吃不下了……明天再喂好不好～", "哈欠连天");
            return;
        }
        bond.Feeds++;
        bond.LastFedUtc = DateTime.UtcNow;
        NoteInteraction();
        AddAffection(snack.Affection);
        SetMood("满足", TimeSpan.FromMinutes(30));
        Say(Pick(snack.Lines), snack.Animation);
    }

    private void ShowSnackPanel()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < Snacks.Length; i++)
        {
            int index = i;
            var button = BigBlueFish.Deployment.ModernUI.Button(Snacks[i].Name, () => { petPanel?.Close(); Feed(index); });
            button.Margin = new Thickness(0, 0, 8, 8);
            button.MinWidth = 72;
            wrap.Children.Add(button);
        }
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = $"今天已经喂了 {bond.Feeds}/6 次", Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(wrap);
        ShowPetPanel("给鱼鱼喂点什么呢？", body, 300, true);
    }

    // ---------------------------------------------------------------- small panels next to her
    private void ShowPetPanel(string title, UIElement body, double width, bool closeOnDeactivate)
    {
        petPanel?.Close();
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = title, Foreground = blue, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 16, MinHeight = 0, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right, Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "关闭" };
        header.Children.Add(close);
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(body);
        var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 251, 244)), BorderBrush = blue,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 12), Child = content };
        var window = new Window { Title = "蓝色大肥鱼", Width = width, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Topmost = this.Topmost, Owner = this, Content = frame, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13,
            WindowStartupLocation = WindowStartupLocation.Manual };
        using (var theme = typeof(BigBlueFish.Deployment.ModernWindow).Assembly.GetManifestResourceStream("BigBlueFish.ModernTheme.xaml"))
        {
            if (theme != null)
            {
                var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(theme);
                window.Resources.MergedDictionaries.Add(resources);
            }
        }
        bool closing = false;
        window.Closing += (_, _) => closing = true;
        void CloseOnce() { if (!closing) window.Close(); }
        close.Click += (_, _) => CloseOnce();
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseOnce(); } };
        if (closeOnDeactivate) window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(() => CloseOnce());
        window.Loaded += (_, _) => PlacePanel(window);
        window.Closed += (_, _) => { if (petPanel == window) petPanel = null; };
        petPanel = window;
        window.Left = -10000; window.Top = -10000;
        window.Show();
        window.Activate();
    }

    private void PlacePanel(Window window)
    {
        var area = CompanionEdgeLayout.WorkArea(this);
        var body = CompanionEdgeLayout.PetBounds(this);
        double width = window.ActualWidth, height = window.ActualHeight;
        double x = Left + body.Left - width - 8;
        if (x < area.Left + 8) x = Left + body.Right + 8;
        double y = Top + body.Bottom - height;
        window.Left = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        window.Top = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8));
    }
}
