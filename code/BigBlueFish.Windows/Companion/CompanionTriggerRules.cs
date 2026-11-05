using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// One trigger rule: when <event> happens (and its condition holds), play one of <actions> and/or say <bubble>.
internal sealed class CompanionTriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "新规则";
    public bool Enabled { get; set; } = true;
    public string Event { get; set; } = "chat.message";
    public string Condition { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    public string Bubble { get; set; } = "";
    public int Priority { get; set; } = 50;
    public int CooldownSeconds { get; set; } = 60;
}

// The events a rule can listen to. Kind: how the condition is read.
internal enum TriggerCondition { None, Text, AtLeast, Hour, Gold }

internal static class CompanionTriggerRules
{
    internal const string FileName = "trigger-rules.json";
    internal const int MaxRules = 50;

    internal static readonly (string Event, string Label, TriggerCondition Kind, string Hint)[] Events =
    [
        ("startup", "鱼鱼启动时", TriggerCondition.None, ""),
        ("return", "主人离开后回来", TriggerCondition.AtLeast, "离开了至少多少分钟（留空 = 10 分钟以上都算）"),
        ("hour", "整点", TriggerCondition.Hour, "几点（0–23，可写多个，如 9,12,18；留空 = 每个整点）"),
        ("app.switch", "切换到某个程序", TriggerCondition.Text, "程序名或窗口标题包含（如 code、chrome、微信；可用 | 分隔多个）"),
        ("click", "点鱼鱼", TriggerCondition.Text, "部位：头 / 身体 / 尾巴（留空 = 任意）"),
        ("pat", "摸鱼鱼的头", TriggerCondition.None, ""),
        ("feed", "喂零食", TriggerCondition.Text, "零食名包含（留空 = 任意）"),
        ("chat.message", "聊天时你说的话", TriggerCondition.Text, "包含的文字（可用 | 分隔多个；留空 = 任意一句）"),
        ("chat.reply", "鱼鱼回答完", TriggerCondition.Text, "回答包含的文字（留空 = 任意）"),
        ("chat.error", "聊天出错 / 鱼鱼走神", TriggerCondition.None, ""),
        ("reminder", "提醒事项到点", TriggerCondition.Text, "提醒内容包含（留空 = 任意）"),
        ("care", "关怀提醒", TriggerCondition.Text, "类型：久坐 / 喝水 / 护眼 / 睡觉（留空 = 任意）"),
        ("focus.start", "开始专注", TriggerCondition.None, ""),
        ("focus.end", "专注结束", TriggerCondition.Text, "完成 / 提前结束（留空 = 任意）"),
        ("game.start", "开始玩游戏", TriggerCondition.Text, "游戏名包含（留空 = 任意）"),
        ("game.end", "游戏结束", TriggerCondition.Text, "游戏名包含（留空 = 任意）"),
        ("delta", "三角洲行动", TriggerCondition.Text, "进图 / 撤离 / 阵亡 / 被击倒（留空 = 任意）"),
        ("gold.change", "金价变动", TriggerCondition.Gold, "可写「涨」或「跌」，再加单次变动至少多少元，如「涨 2」「跌」「1.5」（留空 = 任意变动）"),
        ("gold.alert", "金价到提醒价", TriggerCondition.Text, "高于 / 低于（留空 = 任意）"),
    ];

    internal static readonly (int Value, string Label)[] Priorities = [(20, "低"), (40, "较低"), (50, "中"), (70, "较高"), (90, "高")];
    internal static readonly (int Seconds, string Label)[] Cooldowns =
        [(0, "不限制"), (10, "10 秒"), (30, "30 秒"), (60, "1 分钟"), (300, "5 分钟"), (600, "10 分钟"), (1800, "30 分钟"), (3600, "1 小时"), (86400, "1 天")];

    internal static string EventLabel(string kind) => Events.FirstOrDefault(e => e.Event == kind).Label ?? kind;
    internal static TriggerCondition ConditionKind(string kind) => Events.FirstOrDefault(e => e.Event == kind).Kind;

    internal static List<CompanionTriggerRule> Load()
    {
        try
        {
            if (!File.Exists(FileName)) return new();
            var rules = JsonSerializer.Deserialize<List<CompanionTriggerRule>>(File.ReadAllText(FileName)) ?? new();
            return rules.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id) && Events.Any(e => e.Event == r.Event)).Take(MaxRules)
                .Select(Clean).ToList();
        }
        catch { return new(); }
    }

    internal static CompanionTriggerRule Clean(CompanionTriggerRule r)
    {
        r.Name = string.IsNullOrWhiteSpace(r.Name) ? "规则" : r.Name.Trim()[..Math.Min(40, r.Name.Trim().Length)];
        r.Condition = (r.Condition ?? "").Trim();
        if (r.Condition.Length > 200) r.Condition = r.Condition[..200];
        r.Bubble = (r.Bubble ?? "").Trim();
        if (r.Bubble.Length > 300) r.Bubble = r.Bubble[..300];
        r.Actions = (r.Actions ?? new()).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Take(20).ToList();
        r.Priority = Math.Clamp(r.Priority, 1, 100);
        r.CooldownSeconds = Math.Clamp(r.CooldownSeconds, 0, 86400);
        return r;
    }

    internal static void Save(IEnumerable<CompanionTriggerRule> rules)
    {
        try
        {
            string json = JsonSerializer.Serialize(rules.Take(MaxRules).ToList(), new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(FileName + ".tmp", json);
            File.Move(FileName + ".tmp", FileName, true);
        }
        catch { }
    }

    private static readonly Regex Number = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    // Does the event (subject text, number) meet the rule's condition?
    internal static bool Matches(CompanionTriggerRule rule, string subject, double number)
    {
        string condition = rule.Condition.Trim();
        switch (ConditionKind(rule.Event))
        {
            case TriggerCondition.Text:
                if (condition.Length == 0) return true;
                return condition.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(part => subject.Contains(part, StringComparison.OrdinalIgnoreCase));
            case TriggerCondition.AtLeast:
                return condition.Length == 0 || !double.TryParse(Number.Match(condition).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var least)
                    || number >= least;
            case TriggerCondition.Hour:
                if (condition.Length == 0) return true;
                return Number.Matches(condition).Select(m => int.TryParse(m.Value, out var h) ? h : -1).Contains((int)number);
            case TriggerCondition.Gold:
                if (condition.Contains('涨') && subject != "涨" || condition.Contains('跌') && subject != "跌") return false;
                var amount = Number.Match(condition);
                return !amount.Success || !double.TryParse(amount.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var step) || Math.Abs(number) >= step;
            default:
                return true;
        }
    }

    // {称呼} {时间} {内容} {数值} and any event-specific names (价格, 变动, 程序, 游戏 …).
    internal static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(template, @"\{([^{}]{1,8})\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}

public partial class MainWindow
{
    private List<CompanionTriggerRule> triggerRules = CompanionTriggerRules.Load();
    private readonly Dictionary<string, DateTime> ruleLastFired = new();

    // Something happened: let the owner's rules react. Only the highest-priority matching rule fires.
    // said: what she has just said about it herself (the rule's words are added below it, never over it);
    // important: that line stays until dismissed; ownReaction: her built-in reaction to this event is on
    // screen and the rule's action may replace it. Returns true when a rule fired.
    private bool PetEvent(string kind, string subject = "", double number = double.NaN, IReadOnlyDictionary<string, string>? values = null,
        string? said = null, bool important = false, bool ownReaction = false)
    {
        if (triggerRules.Count == 0 || companionClosed) return false;
        var now = DateTime.UtcNow;
        var rule = triggerRules.Where(r => r.Enabled && r.Event == kind && CompanionTriggerRules.Matches(r, subject, number)
                && (!ruleLastFired.TryGetValue(r.Id, out var last) || (now - last).TotalSeconds >= r.CooldownSeconds))
            .OrderByDescending(r => r.Priority).FirstOrDefault();
        if (rule == null) return false;
        RunTriggerRule(rule, subject, number, values, false, said, important, ownReaction);
        return true;
    }

    private void RunTriggerRule(CompanionTriggerRule rule, string subject, double number, IReadOnlyDictionary<string, string>? values,
        bool test = false, string? said = null, bool important = false, bool ownReaction = false)
    {
        ruleLastFired[rule.Id] = DateTime.UtcNow;
        var fill = new Dictionary<string, string>(values ?? new Dictionary<string, string>())
        {
            ["称呼"] = bond.Address, ["时间"] = DateTime.Now.ToString("HH:mm"), ["内容"] = subject,
            ["数值"] = double.IsNaN(number) ? "" : number.ToString("0.##", CultureInfo.InvariantCulture)
        };
        var clips = rule.Actions.Select(TryFindRule).Where(r => r != null).ToList();
        bool played = false;
        if (clips.Count > 0 && (test ? companionReady && !dragging && !PuppetActive && !exitAnimation : ActionAllowed(rule.Priority, ownReaction)))
        {
            if (test && Crawling) StopCrawlNow(false);
            CancelMovement();
            StopTargetRun();
            PlayAnimation(clips[companionRandom.Next(clips.Count)]!, "Rule");
            playingPriority = rule.Priority;
            played = true;
        }
        if (rule.Bubble.Length > 0)
        {
            string text = CompanionTriggerRules.Fill(rule.Bubble, fill);
            Say(string.IsNullOrEmpty(said) ? text : said + "\n" + text, null, important);
        }
        else if (test && !played) Say(clips.Count == 0 ? "这条规则还没选动作，也没写气泡哦～" : "鱼鱼现在不方便表演～");
    }

    // Hour and foreground-program events, from CareTick.
    private int lastRuleHour = -1;
    private IntPtr lastRuleForeground;
    private string lastRuleProgram = "";

    private void RuleTick(DateTime local)
    {
        if (triggerRules.Count == 0) return;
        if (local.Minute == 0 && local.Hour != lastRuleHour)
        {
            lastRuleHour = local.Hour;
            PetEvent("hour", local.Hour.ToString(CultureInfo.InvariantCulture), local.Hour);
        }
        else if (local.Minute != 0) lastRuleHour = -1;
        if (!triggerRules.Any(r => r.Enabled && r.Event == "app.switch")) return;
        var foreground = CompanionNative.GetForegroundWindow();
        if (foreground == lastRuleForeground || !CompanionNative.IsExternalForegroundCandidate(foreground)) return;
        lastRuleForeground = foreground;
        string program = "";
        try
        {
            CompanionNative.GetWindowThreadProcessId(foreground, out uint pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            program = process.ProcessName;
        }
        catch { }
        var title = new System.Text.StringBuilder(256);
        CompanionNative.GetWindowText(foreground, title, title.Capacity);
        string subject = program + " " + title;
        if (program == lastRuleProgram) return; // same program, another of its windows
        lastRuleProgram = program;
        PetEvent("app.switch", subject, double.NaN, new Dictionary<string, string> { ["程序"] = program, ["标题"] = title.ToString() });
    }

    // ---------------------------------------------------------------- the rules window
    private BigBlueFish.Deployment.ModernWindow? rulesWindow;

    private void ShowTriggerRules()
    {
        if (rulesWindow != null) { rulesWindow.Show(); rulesWindow.Activate(); return; }
        var window = new BigBlueFish.Deployment.ModernWindow("触发规则 · 蓝色大肥鱼", "触发规则",
            "让鱼鱼在某件事发生时演指定的动作、说指定的话。比如：切到 VS Code 时「写代码」，整点时「伸懒腰」并提醒你喝水，"
            + "金价涨了「金袋叮当」。优先级高的规则能打断正在演的规则和聊天状态动作；你拖她、点她、她在跑或爬时不会被打断。", 900, 720);
        window.ShowInTaskbar = true;
        if (companionSettings?.IsVisible == true) window.Owner = companionSettings;
        rulesWindow = window;
        var muted = new SolidColorBrush(Color.FromRgb(96, 106, 130));
        var list = new StackPanel();
        window.Body.Children.Add(list);
        var status = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        window.Body.Children.Add(status);
        window.Body.Children.Add(new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "气泡里可以写 {称呼} {时间} {内容}（触发它的文字，如你说的话、程序名、游戏名）{数值}，金价规则还能用 {价格} {变动}。"
                + $"规则保存在 data\\{CompanionTriggerRules.FileName}，最多 {CompanionTriggerRules.MaxRules} 条。" });

        void Save() => CompanionTriggerRules.Save(triggerRules);
        void Fill()
        {
            list.Children.Clear();
            if (triggerRules.Count == 0)
                list.Children.Add(new TextBlock { Text = "还没有规则。点「新建规则」开始吧～", Foreground = muted, Margin = new Thickness(0, 4, 0, 8) });
            foreach (var rule in triggerRules.ToList())
            {
                var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(214, 220, 234)), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
                var row = new DockPanel();
                card.Child = row;
                var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                System.Windows.Automation.AutomationProperties.SetName(enabled, "启用 " + rule.Name);
                enabled.Click += (_, _) => { rule.Enabled = enabled.IsChecked == true; Save(); };
                DockPanel.SetDock(enabled, Dock.Left);
                row.Children.Add(enabled);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(buttons, Dock.Right);
                Button Small(string text, Action action)
                {
                    var b = BigBlueFish.Deployment.ModernUI.Button(text, action);
                    b.Margin = new Thickness(6, 0, 0, 0); b.Padding = new Thickness(10, 3, 10, 3); b.MinHeight = 0; b.FontSize = 12;
                    buttons.Children.Add(b);
                    return b;
                }
                Small("试一下", () => { RunTriggerRule(rule, "（试触发）", double.NaN, null, true); status.Text = $"已试触发「{rule.Name}」。"; });
                Small("编辑", () => { if (EditTriggerRule(window, rule)) { Save(); Fill(); } });
                Small("删除", () =>
                {
                    if (BigBlueFish.Deployment.ModernUI.Choice(window, "删除规则", $"删除规则「{rule.Name}」？", "删除", "取消") != 0) return;
                    triggerRules.Remove(rule); Save(); Fill();
                });
                row.Children.Add(buttons);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = rule.Name, FontWeight = FontWeights.SemiBold });
                string when = CompanionTriggerRules.EventLabel(rule.Event) + (rule.Condition.Length > 0 ? $"（{rule.Condition}）" : "");
                string what = (rule.Actions.Count > 0 ? "演「" + string.Join("」或「", rule.Actions.Take(4)) + (rule.Actions.Count > 4 ? "」…" : "」") : "不换动作")
                    + (rule.Bubble.Length > 0 ? "，说「" + (rule.Bubble.Length > 30 ? rule.Bubble[..30] + "…" : rule.Bubble) + "」" : "");
                string priority = CompanionTriggerRules.Priorities.OrderBy(p => Math.Abs(p.Value - rule.Priority)).First().Label;
                string cooldown = CompanionTriggerRules.Cooldowns.OrderBy(c => Math.Abs(c.Seconds - rule.CooldownSeconds)).First().Label;
                text.Children.Add(new TextBlock { Text = $"当：{when} → {what}", Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = $"优先级 {priority} · 冷却 {cooldown}", Foreground = muted, FontSize = 11 });
                row.Children.Add(text);
                list.Children.Add(card);
            }
        }
        window.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("新建规则", () =>
        {
            if (triggerRules.Count >= CompanionTriggerRules.MaxRules) { status.Text = $"最多 {CompanionTriggerRules.MaxRules} 条规则。"; return; }
            var rule = new CompanionTriggerRule();
            if (!EditTriggerRule(window, rule)) return;
            triggerRules.Add(rule); Save(); Fill();
        }, true));
        window.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("动作库…", ShowActionLibrary));
        window.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("关闭", window.Close));
        window.Closed += (_, _) => { if (rulesWindow == window) rulesWindow = null; };
        Fill();
        window.Show();
        window.Activate();
    }

    // The editor. Returns true when the owner saved (the rule object is then updated).
    private bool EditTriggerRule(Window owner, CompanionTriggerRule rule)
    {
        var dialog = new BigBlueFish.Deployment.ModernWindow("编辑触发规则 · 蓝色大肥鱼", "编辑触发规则", "选一件事、要演的动作和要说的话。", 680, 860)
        { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var muted = new SolidColorBrush(Color.FromRgb(96, 106, 130));
        var body = dialog.Body;
        TextBlock Label(string text) { var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) }; body.Children.Add(t); return t; }
        TextBox Box(string value, double height = 30)
        {
            var box = new TextBox { Text = value, MinHeight = height, Padding = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = height > 30, VerticalContentAlignment = height > 30 ? VerticalAlignment.Top : VerticalAlignment.Center };
            body.Children.Add(box);
            return box;
        }
        Label("名称");
        var name = Box(rule.Name);
        Label("当");
        var events = new ComboBox();
        foreach (var e in CompanionTriggerRules.Events) events.Items.Add(e.Label);
        events.SelectedIndex = Math.Max(0, Array.FindIndex(CompanionTriggerRules.Events, e => e.Event == rule.Event));
        body.Children.Add(events);
        var conditionLabel = Label("条件");
        var condition = Box(rule.Condition);
        var hint = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        body.Children.Add(hint);
        void EventChanged()
        {
            var e = CompanionTriggerRules.Events[Math.Max(0, events.SelectedIndex)];
            bool has = e.Kind != TriggerCondition.None;
            conditionLabel.Visibility = condition.Visibility = hint.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = e.Hint;
        }
        events.SelectionChanged += (_, _) => EventChanged();
        EventChanged();

        Label("演这些动作之一（可多选；不选就只说话）");
        var actionSearch = new TextBox { MinHeight = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(actionSearch, "筛选动作");
        body.Children.Add(actionSearch);
        var chosen = new HashSet<string>(rule.Actions);
        var actionList = new ListBox { Height = 210, SelectionMode = SelectionMode.Multiple };
        body.Children.Add(actionList);
        var chosenText = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(chosenText);
        bool filling = false;
        void FillActions()
        {
            filling = true;
            actionList.Items.Clear();
            string q = actionSearch.Text.Trim();
            var ids = (originalAnimationPolicy?.Rules ?? animationPolicy.Rules).Select(r => r.Id)
                .Where(id => q.Length == 0 || id.Contains(q, StringComparison.OrdinalIgnoreCase) || ActionCategory(id).Contains(q))
                .OrderBy(id => Array.IndexOf(ActionCategoryOrder, ActionCategory(id))).ThenBy(id => id, StringComparer.CurrentCulture);
            foreach (var id in ids)
            {
                var item = new ListBoxItem { Content = $"{ActionCategory(id)} · {id}", Tag = id };
                actionList.Items.Add(item);
                if (chosen.Contains(id)) item.IsSelected = true;
            }
            filling = false;
            chosenText.Text = chosen.Count == 0 ? "还没选动作。" : "已选：" + string.Join("、", chosen);
        }
        actionList.SelectionChanged += (_, e) =>
        {
            if (filling) return;
            foreach (ListBoxItem item in e.AddedItems) chosen.Add((string)item.Tag);
            foreach (ListBoxItem item in e.RemovedItems) chosen.Remove((string)item.Tag);
            chosenText.Text = chosen.Count == 0 ? "还没选动作。" : "已选：" + string.Join("、", chosen);
        };
        actionSearch.TextChanged += (_, _) => FillActions();
        FillActions();

        Label("同时说（可留空）");
        var bubbleText = Box(rule.Bubble, 54);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        options.Children.Add(new TextBlock { Text = "优先级", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var priority = new ComboBox { Width = 110 };
        foreach (var p in CompanionTriggerRules.Priorities) priority.Items.Add(p.Label);
        priority.SelectedIndex = Array.IndexOf(CompanionTriggerRules.Priorities, CompanionTriggerRules.Priorities.OrderBy(p => Math.Abs(p.Value - rule.Priority)).First());
        options.Children.Add(priority);
        options.Children.Add(new TextBlock { Text = "冷却", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 8, 0) });
        var cooldown = new ComboBox { Width = 110 };
        foreach (var c in CompanionTriggerRules.Cooldowns) cooldown.Items.Add(c.Label);
        cooldown.SelectedIndex = Array.IndexOf(CompanionTriggerRules.Cooldowns, CompanionTriggerRules.Cooldowns.OrderBy(c => Math.Abs(c.Seconds - rule.CooldownSeconds)).First());
        options.Children.Add(cooldown);
        body.Children.Add(options);
        body.Children.Add(new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "聊天状态动作的优先级：倾听 较低、思考 较低到中之间、出错 较高。冷却：这条规则触发后，多久内不再触发。" });

        bool saved = false;
        dialog.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("保存", () =>
        {
            rule.Name = name.Text;
            rule.Event = CompanionTriggerRules.Events[Math.Max(0, events.SelectedIndex)].Event;
            rule.Condition = CompanionTriggerRules.ConditionKind(rule.Event) == TriggerCondition.None ? "" : condition.Text;
            rule.Actions = chosen.ToList();
            rule.Bubble = bubbleText.Text;
            rule.Priority = CompanionTriggerRules.Priorities[Math.Max(0, priority.SelectedIndex)].Value;
            rule.CooldownSeconds = CompanionTriggerRules.Cooldowns[Math.Max(0, cooldown.SelectedIndex)].Seconds;
            CompanionTriggerRules.Clean(rule);
            saved = true;
            dialog.Close();
        }, true));
        dialog.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("取消", dialog.Close));
        dialog.ShowDialog();
        return saved;
    }
}
