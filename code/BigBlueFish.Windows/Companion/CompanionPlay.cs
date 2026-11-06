using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace BigBlueFish.Windows;

// Play: toys she plays with you, shows she performs (magic, dances, music) with applause or an encore,
// rock-paper-scissors, hide-and-seek at a screen edge, and invitations to play when you seem free.
// Playing costs energy, which comes back over time; tired, she would rather rest.
public partial class MainWindow
{
    internal sealed record Toy(string Name, string Clip, int Cost, string? Season, string[] Lines);

    internal static readonly Toy[] Toys =
    [
        new("魔方", "原地专心玩魔方", 10, null, ["魔方！看鱼鱼三十秒还原……唔，再给鱼鱼一分钟！", "转转转～这一面马上就齐啦！"]),
        new("玩具车", "原地蹲下玩玩具汽车", 10, null, ["嘟嘟～小汽车出发啦！", "鱼鱼的小汽车跑得可快了！"]),
        new("陀螺", "抽陀螺", 15, null, ["啪！陀螺转起来啦～", "看鱼鱼抽陀螺，转得稳稳的！"]),
        new("毽子", "踢毽子", 20, null, ["一个、两个、三个……主人帮鱼鱼数着！", "踢毽子鱼鱼最拿手啦～"]),
        new("秋千", "荡秋千", 15, null, ["荡高高～主人帮鱼鱼推一下嘛！", "风吹得好舒服～"]),
        new("木马", "骑木马", 15, null, ["驾～木马跑起来啦！", "摇呀摇，鱼鱼是大骑士！"]),
        new("水枪", "玩水枪", 20, null, ["biu biu～水枪大战！主人小心被喷到哦！", "看招！嘿嘿，全身湿透啦～"]),
        new("气球", "吹气球", 10, null, ["呼——呼——气球越来越大啦……会不会爆呀？", "吹好啦！送给主人一个～"]),
        new("风筝", "放风筝", 20, null, ["风筝飞起来啦！越飞越高～", "线要拉紧哦……哇，好高！"]),
        new("五子棋", "下五子棋", 10, null, ["来下五子棋！鱼鱼先走中间～", "唔……这一步要好好想想……"]),
        new("抛球", "三球抛接", 15, null, ["一、二、三……三个球都接住啦！", "看鱼鱼的杂技！"]),
        new("撸猫", "撸猫", 5, null, ["小猫咪～呼噜呼噜，好软好软……", "撸猫是最治愈的事情啦～"]),
        new("打游戏", "玩游戏气急败坏", 15, null, ["鱼鱼也要打游戏！……啊啊又输了！再来一把！", "这关好难！鱼鱼不服！"]),
        new("堆雪人", "堆雪人", 20, "Winter", ["下雪啦！一起堆个大雪人～", "给雪人戴上帽子……完成！"]),
    ];

    internal static readonly Toy[] Shows =
    [
        new("变鸽子", "变鸽子", 15, null, ["女士们先生们～见证奇迹的时刻！", "鸽子鸽子快出来……"]),
        new("凭空生花", "凭空生花", 15, null, ["请看鱼鱼的手……什么都没有对不对？", "一、二、三——变！"]),
        new("扑克魔术", "扑克魔术", 15, null, ["主人心里想一张牌……鱼鱼猜猜是哪张～", "这张牌就是你想的那张！"]),
        new("女仆舞", "优雅女仆舞", 20, null, ["献给主人一支女仆舞～", "转个圈圈，裙摆飞起来啦～"]),
        new("宅舞", "可爱宅舞", 20, null, ["音乐起！鱼鱼的宅舞时间到～", "比心心～跟着鱼鱼一起跳！"]),
        new("摇摆舞", "轻快摇摆舞", 15, null, ["摇呀摇～轻快的小舞曲！", "跟着节奏一起摆～"]),
        new("小提琴", "小提琴演奏", 15, null, ["为主人拉一首小提琴曲～", "嘘……认真听哦"]),
        new("吹笛子", "吹笛子", 10, null, ["悠扬的笛声送给主人～", "呜～呜～这首曲子好听吗？"]),
        new("哼歌", "悠闲哼歌", 5, null, ["啦啦啦～鱼鱼给主人哼首歌～", "哼哼～心情好就想唱歌！"]),
        new("泡泡秀", "鲸鱼吐泡泡特效", 10, null, ["噗噗噗～泡泡秀开始啦！", "好多泡泡，亮晶晶的！"]),
        new("烟花秀", "放烟花", 20, null, ["咻——砰！烟花秀献给主人～", "好漂亮的烟花！"]),
        new("蓝鲸现世", "蓝鲸现世", 25, null, ["压轴节目——蓝鲸现世！", "看好啦，这是鱼鱼的真身！"]),
    ];

    private bool PlayReady(int cost)
    {
        if (!companionReady || exitAnimation || PuppetActive || dragging || hiddenByUser) return false;
        if (Energy() >= cost + 5) return true;
        Say(Pick(new[] { "鱼鱼玩累了……让我歇一会儿好不好～", "呼……没力气了，等鱼鱼充充电再玩！" }), "哈欠连天");
        return false;
    }

    private void SpendPlay(int cost, bool counted = true)
    {
        bond.RollDay(DateTime.Now);
        double energy = Energy();
        bond.Energy = Math.Max(0, energy - cost);
        bond.EnergyUtc = DateTime.UtcNow;
        if (counted) { bond.Plays++; AddAffection(bond.Plays <= 10 ? 1 : .2); }
        NoteInteraction();
        SetMood("开心", TimeSpan.FromMinutes(20));
        SaveBondSoon();
    }

    // Starts a clip for play: off the edge / out of a crawl first.
    private bool PlayClip(string clip, string phase)
    {
        if (ClipOr(clip, "点击回应-开心跃动") is not { } rule) return false; // an appearance pack may lack the clip
        LeaveEdgeHide();
        if (Crawling) StopCrawlNow(false);
        StopTargetRun();
        CancelMovement();
        PlayAnimation(rule, phase);
        return true;
    }

    private void PlayToy(Toy toy)
    {
        if (!PlayReady(toy.Cost) || !PlayClip(toy.Clip, "Toy")) return;
        SpendPlay(toy.Cost);
        string line = Pick(toy.Lines);
        if (toy.Name == "水枪" && CompanionAnimationPolicy.Season(DateTime.Now) == "Summer") line += "夏天玩水最舒服啦！";
        Say(line);
        PetEvent("play", toy.Name, said: line, ownReaction: true);
    }

    private Toy? lastShow;

    private void PerformShow(Toy? show = null)
    {
        show ??= Pick(Shows.Where(s => s != lastShow).ToArray());
        if (!PlayReady(show.Cost) || !PlayClip(show.Clip, "Perform")) return;
        lastShow = show;
        SpendPlay(show.Cost);
        string line = Pick(show.Lines);
        Say(line);
        PetEvent("play", show.Name, said: line, ownReaction: true);
    }

    // AnimationFinished: after a show she waits for applause.
    private bool PlayAnimationFinished()
    {
        if (animationPhase != "Perform") return false;
        if (hiddenByUser || fullscreenHidden || dragging || PuppetActive) { AnimationBoundary(); return true; }
        PlayIdle();
        SayWithActions("表演完毕～主人觉得怎么样？", null,
            ("鼓掌", () =>
            {
                AddAffection(.5);
                Say(Pick(new[] { "谢谢主人捧场～鱼鱼会继续努力的！", "嘿嘿，被夸奖了好开心！" }), "女仆屈膝礼仪");
            }),
            ("再来一个", () => PerformShow()),
            ("谢谢鱼鱼", () => Say("不客气～想看随时叫鱼鱼哦！", "点击回应-元气挥手")));
        return true;
    }

    // ---------------------------------------------------------------- rock-paper-scissors
    private static readonly string[] Hands = ["石头", "剪刀", "布"];

    private void ShowRpsPanel()
    {
        var body = new StackPanel();
        var muted = Brushes.SlateGray;
        var score = new TextBlock { Foreground = muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        var result = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "出拳吧！" };
        void Score() => score.Text = $"今天：鱼鱼赢 {bond.RpsWins} 局，主人赢 {bond.RpsLosses} 局 · 精力 {Energy():0}";
        Score();
        body.Children.Add(score);
        var row = new WrapPanel();
        for (int i = 0; i < Hands.Length; i++)
        {
            int mine = i;
            var button = BigBlueFish.Deployment.ModernUI.Button(Hands[i], () =>
            {
                if (!PlayReady(3)) { result.Text = "鱼鱼累了，先休息一下吧～"; return; }
                bond.RollDay(DateTime.Now);
                int hers = companionRandom.Next(3);
                // 0 石头 beats 1 剪刀 beats 2 布 beats 0 石头
                int outcome = mine == hers ? 0 : (hers + 1) % 3 == mine ? 1 : -1; // 1: she wins
                string say;
                if (outcome == 1) { bond.RpsWins++; say = Pick(new[] { "耶！鱼鱼赢啦～", "嘿嘿，鱼鱼猜到你会出这个！" }); PlayClip("点击回应-开心跃动", "Rps"); }
                else if (outcome == -1) { bond.RpsLosses++; say = Pick(new[] { "哼！再来一局！", "呜……怎么又输了！不服不服！" }); PlayClip(companionRandom.NextDouble() < .5 ? "点击回应-傲娇生气" : "玩游戏气急败坏", "Rps"); }
                else { say = Pick(new[] { "平局！心有灵犀～", "一样的！再来！" }); PlayClip("东张西望", "Rps"); }
                SpendPlay(3, counted: false);
                if (bond.RpsWins + bond.RpsLosses == 1) { bond.Plays++; AddAffection(1); }
                string line = $"你出{Hands[mine]}，鱼鱼出{Hands[hers]}——{say}";
                result.Text = line;
                Say(line);
                Score();
                PetEvent("play", "猜拳" + (outcome == 1 ? "输了" : outcome == -1 ? "赢了" : "平局"), said: line, ownReaction: true);
            }, i == 0);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.MinWidth = 64;
            row.Children.Add(button);
        }
        body.Children.Add(row);
        body.Children.Add(result);
        ShowPetPanel("和鱼鱼猜拳", body, 300, false);
    }

    // ---------------------------------------------------------------- hide-and-seek
    // She hides just beyond a random edge of her monitor; bring the cursor to that edge to make her peek, then
    // click her. She gives a tiny hint now and then and comes out by herself after a minute.
    private void StartHideAndSeek()
    {
        if (!PlayReady(10) || PresentationSource.FromVisual(this) == null) return;
        SpendPlay(10);
        LeaveEdgeHide();
        if (Crawling) StopCrawlNow(false);
        StopTargetRun();
        CancelMovement();
        Say("捉迷藏！主人闭上眼睛数三下——鱼鱼要藏起来啦～", "点击回应-开心跃动");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!companionReady || exitAnimation || PuppetActive || dragging || hiddenByUser) return;
            var body = UprightVisibleBounds;
            var centre = PointToScreen(new Point(body.X + body.Width / 2, body.Y + body.Height / 2));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)centre.X, (int)centre.Y));
            // Only an outer edge: beyond an edge shared with another monitor she would simply be on that monitor.
            var spots = OuterEdges(screen);
            if (spots.Count == 0) spots = Forms.Screen.AllScreens.SelectMany(OuterEdges).ToList();
            if (spots.Count == 0) { Say("唔……这里没有地方可以藏，下次再玩吧～"); return; }
            (screen, int edge) = spots[companionRandom.Next(spots.Count)];
            var rect = MonitorRect(screen.Bounds);
            double along = edge is 0 or 2
                ? rect.Top - body.Top + companionRandom.NextDouble() * Math.Max(0, rect.Height - body.Height)
                : rect.Left - body.Left + companionRandom.NextDouble() * Math.Max(0, rect.Width - body.Width);
            speech.Cancel();
            bubble.Visibility = Visibility.Collapsed;
            edgeHide = new EdgeHideState { Edge = edge, Screen = rect, Along = along, Seek = true, SeekStartUtc = DateTime.UtcNow, AwayAt = DateTime.MinValue };
            PlaceAtEdge();
            PlayIdle();
            notifyIcon?.ShowBalloonTip(4000, "蓝色大肥鱼 · 捉迷藏", "鱼鱼藏好啦！把鼠标移到屏幕边上找找她，看到了就点她～", Forms.ToolTipIcon.Info);
        };
        timer.Start();
    }

    private bool SeekActive => edgeHide is { Seek: true };

    private static System.Collections.Generic.List<(Forms.Screen Screen, int Edge)> OuterEdges(Forms.Screen screen)
    {
        var b = screen.Bounds;
        var list = new System.Collections.Generic.List<(Forms.Screen, int)>();
        for (int edge = 0; edge < 4; edge++)
        {
            bool free = true;
            for (int k = 1; k <= 3 && free; k++)
            {
                int x = edge switch { 0 => b.Left - 24, 2 => b.Right + 24, _ => b.Left + b.Width * k / 4 };
                int y = edge switch { 1 => b.Top - 24, 3 => b.Bottom + 24, _ => b.Top + b.Height * k / 4 };
                free = !Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(x, y));
            }
            if (free) list.Add((screen, edge));
        }
        return list;
    }

    // Found: by a click on her peeking, or by dragging her out.
    private void SeekFound(bool dragged)
    {
        var e = edgeHide;
        if (e is not { Seek: true }) return;
        double seconds = (DateTime.UtcNow - e.SeekStartUtc).TotalSeconds;
        edgeHide = null;
        if (!dragged) { ClampCompanion(); anchor = new Point(Left, Top); SaveCompanion(); }
        AddAffection(1);
        var (line, clip) = seconds < 12 ? ("这么快就被找到了！主人好厉害～", "点击回应-害羞惊讶")
            : seconds < 35 ? ("被找到啦～主人眼睛真尖！", "点击回应-开心跃动") : ("终于找到鱼鱼啦！刚刚好紧张～", "点击回应-元气挥手");
        line += $"（用了 {seconds:0} 秒）";
        Say(line, dragged ? null : clip);
        PetEvent("play", "捉迷藏被找到", seconds, said: line, ownReaction: !dragged);
    }

    // ---------------------------------------------------------------- the play panel
    private void ShowPlayPanel()
    {
        bond.RollDay(DateTime.Now);
        double energy = Energy();
        var body = new StackPanel();
        var muted = Brushes.SlateGray;
        body.Children.Add(new TextBlock { Text = $"精力 {energy:0}/100（{EnergyWord(energy)}）· 今天玩了 {bond.Plays} 次 · 心情：{CurrentMood}", Foreground = muted, FontSize = 12 });
        body.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = energy, Height = 6, Margin = new Thickness(0, 4, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(96, 180, 120)) });
        string season = CompanionAnimationPolicy.Season(DateTime.Now);
        WrapPanel Group(string title)
        {
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            var wrap = new WrapPanel();
            body.Children.Add(wrap);
            return wrap;
        }
        void Add(WrapPanel wrap, string label, Action action, bool primary = false, string? tip = null)
        {
            var button = BigBlueFish.Deployment.ModernUI.Button(label, () => { petPanel?.Close(); action(); }, primary);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Padding = new Thickness(10, 3, 10, 3);
            button.MinWidth = 0;
            if (tip != null) button.ToolTip = tip;
            wrap.Children.Add(button);
        }
        var toys = Group("陪她玩");
        foreach (var toy in Toys.Where(t => t.Season == null || t.Season == season)) { var t = toy; Add(toys, t.Name, () => PlayToy(t), tip: $"消耗精力 {t.Cost}"); }
        var shows = Group("看她表演");
        Add(shows, "随便来一个", () => PerformShow(), true);
        foreach (var show in Shows) { var s = show; Add(shows, s.Name, () => PerformShow(s), tip: $"消耗精力 {s.Cost}"); }
        var games = Group("小游戏");
        Add(games, "猜拳", () => Dispatcher.BeginInvoke(ShowRpsPanel), true);
        Add(games, "捉迷藏", StartHideAndSeek, tip: "她藏到屏幕某条边外，把鼠标移到边上找她，看到就点她");
        body.Children.Add(new TextBlock { Text = "精力每小时恢复 15 点；表演完可以鼓掌或者喊「再来一个」。", Foreground = muted, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        ShowPetPanel("陪鱼鱼玩", body, 380, true);
    }

    // ---------------------------------------------------------------- CareTick: invitations, hide-and-seek clock
    private DateTime nextPlayInvite = DateTime.UtcNow.AddMinutes(20), nextInviteCheck;

    private void PlayTick(DateTime now, DateTime local, double idle)
    {
        if (edgeHide is { Seek: true } seek)
        {
            double seconds = (now - seek.SeekStartUtc).TotalSeconds;
            if (seconds is >= 25 and < 26 || seconds is >= 45 and < 46) seek.HintUntil = now.AddSeconds(1.2); // a quick peek as a hint
            if (seconds >= 60)
            {
                LeaveEdgeHide();
                Say("嘿嘿，鱼鱼赢啦～主人没找到我！下次藏得更好！", "点击回应-开心跃动");
                PetEvent("play", "捉迷藏没被找到", seconds);
            }
            return;
        }
        if (!preferences.PlayInvites || now < nextPlayInvite || now < nextInviteCheck) return;
        nextInviteCheck = now.AddSeconds(30);
        // Free and present (not typing away, not gone), daytime, nobody played with her for a while, and she has energy.
        if (!CanChatter || GameActive || FocusActive || PuppetActive || edgeHide != null || bubble.Visibility == Visibility.Visible
            || idle < 5 || idle > 120 || local.Hour < 9 || local.Hour >= 22 || (now - bond.LastInteractionUtc).TotalMinutes < 45
            || Energy() < 50 || animationPhase is not ("Idle" or "Event") || companionRandom.NextDouble() > .25) return;
        nextPlayInvite = now.AddHours(2.5);
        string season = CompanionAnimationPolicy.Season(local);
        var toy = Pick(Toys.Where(t => (t.Season == null || t.Season == season) && t.Cost <= 15).ToArray());
        SayWithActions($"{bond.Address}～陪鱼鱼玩一会儿{toy.Name}好不好？", toy.Clip,
            ("好呀", () => PlayToy(toy)),
            ("玩点别的…", ShowPlayPanel),
            ("等会儿", () => { nextPlayInvite = DateTime.UtcNow.AddHours(3); Say("好吧……那鱼鱼自己先玩一会儿～主人忙完记得来找我哦！"); }));
    }
}
