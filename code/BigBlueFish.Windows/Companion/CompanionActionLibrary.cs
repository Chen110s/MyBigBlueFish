using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// 动作库: every action by category, a live preview, and which pool it is in (random actions, click
// replies, idle, or never on its own). Also adds the four clips taken from luweiyabo/dsh-whale-pet.
public partial class MainWindow
{
    // Clips from luweiyabo/dsh-whale-pet (assets/thumb), copied into assets\fish by the build script.
    // They are only used on purpose (甩出、跑过去、倾听、打招呼) unless the owner puts them in a pool.
    internal static readonly (string Id, string Pool)[] ExtraClips =
        [("预备姿势奔跑", "Special"), ("变成球", "Special"), ("侧耳倾听", "Special"), ("原地挥手打招呼", "Special")];

    private static readonly string[] ChosenPools = ["Event", "Click", "Idle"];
    private IReadOnlyList<CompanionAnimationRule>? loadedAnimationRules;

    private static CompanionAnimationRule CopyRule(CompanionAnimationRule rule) =>
        JsonSerializer.Deserialize<CompanionAnimationRule>(JsonSerializer.Serialize(rule))!;

    // The original rules plus the extra clips that are installed, with the owner's pool choices applied.
    // A choice that would leave the random, click or idle pool without an always-available clip is ignored.
    private CompanionAnimationPolicy BuildBasePolicy(IReadOnlyList<CompanionAnimationRule> loaded)
    {
        loadedAnimationRules = loaded;
        var rules = loaded.Select(CopyRule).ToList();
        foreach (var (id, pool) in ExtraClips)
            if (rules.All(r => r.Id != id) && File.Exists(Path.Combine(AnimationDirectory, id + ".webm")))
                rules.Add(new CompanionAnimationRule { Id = id, File = id + ".webm", Pool = pool, Weight = 100 });
        var basePools = rules.ToDictionary(r => r.Id, r => r.Pool);
        foreach (var rule in rules)
            if (preferences.PoolOverrides.TryGetValue(rule.Id, out var pool) && pool != rule.Pool)
            {
                rule.Pool = pool;
                if (pool == "Click" || pool == "Idle") rule.MoveKind = null; // a walking clip plays in place there
            }
        // Walking clips only play in certain minutes and hours, so they do not keep a pool alive.
        static bool Always(CompanionAnimationRule r) => r.Weight > 0 && r.Festival == null && r.SeasonOnly == null && !r.NightOnly && r.MoveKind == null;
        foreach (var pool in ChosenPools)
            if (!rules.Any(r => r.Pool == pool && Always(r)))
                foreach (var rule in rules.Where(r => basePools[r.Id] == pool))
                {
                    rule.Pool = pool;
                    preferences.PoolOverrides.Remove(rule.Id);
                }
        return new CompanionAnimationPolicy(rules);
    }

    // After the pools changed: rebuild the base and re-apply the appearance pack.
    private void RebuildAnimationPolicy()
    {
        if (loadedAnimationRules == null) return;
        originalAnimationPolicy = BuildBasePolicy(loadedAnimationRules);
        ApplySkin(preferences.Skin, false);
    }

    // A clip by id, or the fallback when the extra clip is not installed.
    private CompanionAnimationRule? ClipOr(string id, string? fallback = null) =>
        TryFindRule(id) ?? (fallback == null ? null : TryFindRule(fallback));

    private string BasePool(string id) =>
        loadedAnimationRules?.FirstOrDefault(r => r.Id == id)?.Pool ?? ExtraClips.FirstOrDefault(c => c.Id == id).Pool ?? "Event";

    // ---------------------------------------------------------------- categories (dsh-whale-pet's, in Chinese)
    private static readonly string[] ActionCategoryOrder =
        ["待机", "转向", "移动", "点击回应", "拖拽", "日常", "工作", "游戏", "音乐舞蹈", "美食", "节日", "四季", "魔术", "趣味", "鲸鱼特色", "梗图", "余额", "碎碎念", "其他"];

    private static readonly Dictionary<string, string> ActionCategories = BuildActionCategories();

    private static Dictionary<string, string> BuildActionCategories()
    {
        var map = new Dictionary<string, string>();
        void Add(string category, params string[] ids) { foreach (var id in ids) map[id] = category; }
        Add("待机", "待机呼吸休闲");
        Add("转向", "东张西望");
        Add("移动", "原地左转奔跑", "原地漂浮踏步", "螃蟹走路", "预备姿势奔跑");
        Add("点击回应", "点击回应-傲娇生气", "点击回应-元气挥手", "点击回应-害羞惊讶", "点击回应-开心跃动", "点击回应-挠痒咯咯笑", "原地挥手打招呼");
        Add("拖拽", "被鼠标拖拽悬空反馈", "变成球");
        Add("日常", "原地小憩沉眠", "哈欠连天", "女仆屈膝礼仪", "打瞌睡被惊醒", "整体换装试色", "晨间刷牙", "照镜子", "超大伸懒腰", "小幅度原地360度旋转展示", "侧耳倾听");
        Add("工作", "写代码", "深度思考碎碎念", "轻快记录", "工作状态-原地踱步张望", "工作状态-垂头叹气冒汗", "工作状态-忙碌点按",
            "工作状态-思考冒泡", "工作状态-清点归档", "工作状态-雀跃庆祝");
        Add("游戏", "下五子棋", "抽陀螺", "玩水枪", "玩游戏气急败坏", "荡秋千", "踢毽子", "骑木马", "原地专心玩魔方", "原地蹲下玩玩具汽车");
        Add("音乐舞蹈", "优雅女仆舞", "可爱宅舞", "吹笛子", "小提琴演奏", "轻快摇摆舞", "悠闲哼歌");
        Add("美食", "偷吃零食被抓住", "吃冰淇淋融化", "吃午餐", "吃大闸蟹", "吃早餐", "吃晚餐", "吃白饭", "吃糖葫芦", "吃西瓜", "吃长寿面", "大口吃零食", "涮火锅");
        Add("节日", "中秋赏月吃月饼", "写福字", "吃年糕", "吃汤圆", "吃粽子", "吃腊八粥", "吃重阳糕", "吃青团", "吃饺子", "拆礼物", "插茱萸赏菊",
            "收红包", "放孔明灯", "放河灯", "放烟花", "穿针乞巧", "舞狮头", "萌化小幽灵", "装点圣诞树", "讨糖南瓜灯");
        Add("四季", "堆雪人", "摇扇纳凉", "放风筝", "被落叶淹没");
        Add("魔术", "凭空生花", "变鸽子", "扑克魔术");
        Add("趣味", "三球抛接", "动物环绕", "原地跳跃抓碎头顶物品", "吹气球", "撸猫", "蝴蝶蜜蜂环绕头顶开花", "被吓一跳", "原地敲击桌面互动", "原地重力下蹲压缩");
        Add("鲸鱼特色", "用鲸鱼尾巴拍打地面", "蓝鲸现世", "鲸鱼吐泡泡特效");
        Add("梗图", "是啊，吃什么", "吃Token");
        Add("余额", "余额-分文不剩", "余额-数金皱眉", "余额-袋空如洗", "余额-金袋叮当", "余额-钱袋如常", "余额-钱袋满溢");
        Add("碎碎念", "碎碎念-发呆碎碎念", "碎碎念-对屏碎碎念", "碎碎念-擦桌碎碎念");
        return map;
    }

    internal static string ActionCategory(string id) => ActionCategories.TryGetValue(id, out var c) ? c : "其他";

    private static string PoolLabel(string pool) => pool switch
    {
        "Event" => "随机动作", "Click" => "点击回应", "Idle" => "待机", "Disabled" => "不自动播放",
        "Greeting" => "启动问候", "Drag" => "被拖动", "Daily" => "每日（刷牙）", "Special" => "专用", _ => pool
    };

    // ---------------------------------------------------------------- the window
    private BigBlueFish.Deployment.ModernWindow? actionLibrary;

    private void ShowActionLibrary()
    {
        if (originalAnimationPolicy == null || !companionReady) { Say("鱼鱼的动画还没准备好，等一下下再看动作库吧～"); return; }
        if (actionLibrary != null)
        {
            if (actionLibrary.WindowState == WindowState.Minimized) actionLibrary.WindowState = WindowState.Normal;
            actionLibrary.Show(); actionLibrary.Activate();
            return;
        }
        var window = new BigBlueFish.Deployment.ModernWindow("动作库 · 蓝色大肥鱼", "动作库",
            "按分类浏览鱼鱼的全部动作，点一个就能预览；也可以让她马上演一遍，或者决定它出现在哪个动作池里。"
            + "「随机动作」会不时自己播放，「点击回应」在你点她时出现，「待机」是平时的呼吸状态，「不自动播放」只在被叫到时才演。", 980, 820);
        window.ShowInTaskbar = true;
        actionLibrary = window;
        var muted = new SolidColorBrush(Color.FromRgb(96, 106, 130));

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var search = new TextBox { Width = 260, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索动作");
        var category = new ComboBox { Width = 160, Margin = new Thickness(10, 0, 0, 0) };
        category.Items.Add("全部分类");
        foreach (var c in ActionCategoryOrder) category.Items.Add(c);
        category.SelectedIndex = 0;
        var poolFilter = new ComboBox { Width = 170, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var f in new[] { "所有动作池", "随机动作", "点击回应", "待机", "不自动播放", "专用" }) poolFilter.Items.Add(f);
        poolFilter.SelectedIndex = 0;
        filters.Children.Add(new TextBlock { Text = "搜索", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        filters.Children.Add(search);
        filters.Children.Add(category);
        filters.Children.Add(poolFilter);
        window.Body.Children.Add(filters);

        var grid = new Grid { Height = 470 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new ListBox { BorderBrush = new SolidColorBrush(Color.FromRgb(214, 220, 234)), Background = Brushes.White };
        System.Windows.Automation.AutomationProperties.SetName(list, "动作列表");
        grid.Children.Add(list);

        var detail = new StackPanel();
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);
        var previewImage = new Image { Width = 448, Height = 252, Stretch = Stretch.Uniform };
        var previewFrame = new Border { Background = new SolidColorBrush(Color.FromRgb(226, 233, 246)), CornerRadius = new CornerRadius(10),
            Width = 448, Height = 252, HorizontalAlignment = HorizontalAlignment.Left, Child = previewImage };
        detail.Children.Add(previewFrame);
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 2) };
        var info = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        detail.Children.Add(title);
        detail.Children.Add(info);
        var poolRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        poolRow.Children.Add(new TextBlock { Text = "放在", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var poolChoice = new ComboBox { Width = 200 };
        System.Windows.Automation.AutomationProperties.SetName(poolChoice, "动作池");
        poolRow.Children.Add(poolChoice);
        detail.Children.Add(poolRow);
        var buttons = new WrapPanel();
        detail.Children.Add(buttons);
        var status = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        detail.Children.Add(status);
        window.Body.Children.Add(grid);
        window.Body.Children.Add(new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            Text = "「预备姿势奔跑」「变成球」「侧耳倾听」「原地挥手打招呼」四个动作来自 luweiyabo/dsh-whale-pet（动画素材仅限开源、非商业使用，见 assets\\fish\\dsh-whale-pet-LICENSE.txt），"
                + "分别用在点屏幕让她跑过去、快速甩出、聊天打字和到达打招呼。换了形象包时，预览和表演用的是形象包里的同名动作（没有的用待机代替）。" });

        CompanionVideoPlayer? preview = null;
        string? selected = null;
        try { preview = new CompanionVideoPlayer(previewImage); }
        catch (Exception ex) { status.Text = "预览不可用：" + ex.Message; }
        if (preview != null) preview.Failed += error => status.Text = "预览失败：" + error;

        IEnumerable<CompanionAnimationRule> Rules() => originalAnimationPolicy?.Rules ?? Enumerable.Empty<CompanionAnimationRule>();
        string ItemText(CompanionAnimationRule r) => $"{r.Id}　·　{PoolLabel(r.Pool)}";

        void Fill()
        {
            string query = search.Text.Trim();
            string? cat = category.SelectedIndex > 0 ? (string)category.SelectedItem : null;
            string? poolWanted = poolFilter.SelectedIndex switch { 1 => "Event", 2 => "Click", 3 => "Idle", 4 => "Disabled", 5 => "Special", _ => null };
            var shown = Rules().Where(r => (query.Length == 0 || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || ActionCategory(r.Id).Contains(query))
                    && (cat == null || ActionCategory(r.Id) == cat)
                    && (poolWanted == null || (poolWanted == "Special" ? r.Pool is not ("Event" or "Click" or "Idle" or "Disabled") : r.Pool == poolWanted)))
                .OrderBy(r => Array.IndexOf(ActionCategoryOrder, ActionCategory(r.Id))).ThenBy(r => r.Id, StringComparer.CurrentCulture).ToList();
            list.Items.Clear();
            foreach (var r in shown) list.Items.Add(new ListBoxItem { Content = ItemText(r), Tag = r.Id, Padding = new Thickness(8, 5, 8, 5) });
            int index = shown.FindIndex(r => r.Id == selected);
            list.SelectedIndex = index >= 0 ? index : shown.Count > 0 ? 0 : -1;
            if (shown.Count == 0) { title.Text = "没有找到动作"; info.Text = ""; }
        }

        bool updatingChoice = false;
        void Select(string id)
        {
            selected = id;
            var rule = Rules().FirstOrDefault(r => r.Id == id);
            if (rule == null) return;
            title.Text = id;
            string basePool = BasePool(id);
            var shownRule = animationPolicy.Rules.FirstOrDefault(r => r.Id == id);
            bool substituted = activeSkin != null && shownRule != null && !activeSkin.Files.ContainsKey(id) && activeSkin.UseIdleForMissing;
            info.Text = $"分类：{ActionCategory(id)}　·　默认：{PoolLabel(basePool)}" + (rule.MoveKind != null ? "　·　会边走边演" : "")
                + (rule.Festival != null || rule.SeasonOnly != null || rule.NightOnly ? "　·　有季节 / 节日 / 夜间限制" : "")
                + (substituted ? $"\n当前形象包「{activeSkin!.Name}」没有这个动作，会用它的待机代替。" : "");
            updatingChoice = true;
            poolChoice.Items.Clear();
            var options = new List<(string Pool, string Label)> { ("Event", "随机动作"), ("Click", "点击回应"), ("Idle", "待机"), ("Disabled", "不自动播放") };
            if (basePool is not ("Event" or "Click" or "Idle" or "Disabled")) options.Insert(0, (basePool, "原用途：" + PoolLabel(basePool)));
            foreach (var (pool, label) in options) poolChoice.Items.Add(new ComboBoxItem { Content = label, Tag = pool });
            poolChoice.SelectedIndex = Math.Max(0, options.FindIndex(o => o.Pool == rule.Pool));
            updatingChoice = false;
            status.Text = "";
            if (preview != null && shownRule != null)
            {
                string file = Path.Combine(AnimationDirectory, shownRule.File);
                void Loop()
                {
                    if (selected != id || actionLibrary != window) return;
                    try { preview.Play(file, false, Loop, normalize: IsSkinClip(shownRule)); }
                    catch (Exception ex) { status.Text = "预览失败：" + ex.Message; }
                }
                Loop();
            }
        }

        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: string id }) Select(id); };
        search.TextChanged += (_, _) => Fill();
        category.SelectionChanged += (_, _) => Fill();
        poolFilter.SelectionChanged += (_, _) => Fill();
        poolChoice.SelectionChanged += (_, _) =>
        {
            if (updatingChoice || selected == null || poolChoice.SelectedItem is not ComboBoxItem { Tag: string pool }) return;
            string id = selected;
            if (pool == BasePool(id)) preferences.PoolOverrides.Remove(id); else preferences.PoolOverrides[id] = pool;
            RebuildAnimationPolicy();
            SaveCompanion();
            var now = Rules().FirstOrDefault(r => r.Id == id);
            if (now != null && now.Pool != pool)
                status.Text = "这个池里得至少留一个随时能播放的动作，所以没有改。";
            else status.Text = $"已放到「{PoolLabel(pool)}」。";
            if (list.SelectedItem is ListBoxItem item && now != null) item.Content = ItemText(now);
        };

        var play = BigBlueFish.Deployment.ModernUI.Button("让鱼鱼演一遍", () =>
        {
            if (selected == null) return;
            if (!companionReady || exitAnimation || PuppetActive || dragging || hiddenByUser) { status.Text = "鱼鱼现在不方便表演～"; return; }
            if (Crawling) StopCrawlNow(false);
            CancelMovement();
            StopTargetRun();
            if (TryFindRule(selected) is { } rule) PlayAnimation(rule, "Preview");
        }, true);
        play.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(play);
        var reset = BigBlueFish.Deployment.ModernUI.Button("全部恢复默认", () =>
        {
            if (preferences.PoolOverrides.Count == 0) { status.Text = "现在就是默认的动作池。"; return; }
            if (BigBlueFish.Deployment.ModernUI.Choice(window, "恢复默认动作池", $"把 {preferences.PoolOverrides.Count} 个改过的动作放回原来的动作池？", "恢复", "取消") != 0) return;
            preferences.PoolOverrides.Clear();
            RebuildAnimationPolicy();
            SaveCompanion();
            Fill();
            status.Text = "已恢复默认。";
        });
        reset.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(reset);
        var rulesButton = BigBlueFish.Deployment.ModernUI.Button("触发规则…", ShowTriggerRules);
        rulesButton.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(rulesButton);

        window.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("关闭", window.Close));
        window.Closed += (_, _) =>
        {
            selected = null;
            preview?.Dispose();
            if (actionLibrary == window) actionLibrary = null;
        };
        Fill();
        window.Show();
        window.Activate();
    }
}
