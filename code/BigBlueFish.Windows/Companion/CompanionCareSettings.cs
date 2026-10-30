using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// Settings page「互动与关怀」.
public partial class MainWindow
{
    private void AddCareSettings(StackPanel page)
    {
        var blue = new SolidColorBrush(Color.FromRgb(53, 76, 129));
        void Section(string title) => page.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = blue, Margin = new Thickness(0, page.Children.Count > 1 ? 18 : 0, 0, 6) });
        void Note(string text) => page.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        CheckBox Toggle(string label, bool value, Action<bool> change, Panel? target = null)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
            box.Click += (_, _) => { change(box.IsChecked == true); SaveCompanion(); };
            (target ?? page).Children.Add(box);
            return box;
        }
        ComboBox Minutes(int[] options, int current, Action<int> change, string suffix = " 分钟")
        {
            var combo = new ComboBox { MinWidth = 96, Margin = new Thickness(10, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            foreach (var option in options) combo.Items.Add(option + suffix);
            int nearest = Array.IndexOf(options, options.OrderBy(o => Math.Abs(o - current)).First());
            combo.SelectedIndex = nearest;
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { change(options[combo.SelectedIndex]); SaveCompanion(); } };
            return combo;
        }
        void ToggleWithMinutes(string label, bool value, Action<bool> change, int[] options, int current, Action<int> changeMinutes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            Toggle(label, value, change, row);
            row.Children.Add(Minutes(options, current, changeMinutes));
            page.Children.Add(row);
        }

        // ---- bond
        Section("鱼鱼的心意");
        bond.RollDay(DateTime.Now);
        page.Children.Add(new TextBlock { Text = $"好感度 {bond.Affection:0}/100 · {bond.Level} · 现在的心情：{CurrentMood}", Margin = new Thickness(0, 0, 0, 6) });
        page.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = bond.Affection, Height = 10, Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(96, 132, 214)) });
        Note($"今天：摸头 {bond.Pats} 次 · 喂食 {bond.Feeds} 次 · 聊天 {bond.Chats} 句 · 专注 {bond.Focus} 轮 · 一共陪伴了 {bond.CompanionMinutes / 60:0.#} 小时"
            + (string.IsNullOrWhiteSpace(bond.Nickname) ? "" : $" · 鱼鱼叫你「{bond.Nickname}」"));
        Note("摸头：鼠标在鱼鱼头上来回轻轻划几下；连续戳她会生气哦。右键菜单可以喂零食、聊天、专注和设置提醒。");

        // ---- chat & memory
        Section("聊天与记忆");
        Toggle("双击鱼鱼可以和她聊天（使用本地模型，内容不上传）", preferences.ChatEnabled, v => { preferences.ChatEnabled = v; if (!v) chatWindow?.Close(); });
        Toggle("记住聊过的内容（只保存在本机，会像人一样慢慢淡忘、记混）", preferences.MemoryEnabled, v => preferences.MemoryEnabled = v);
        var memoryInfo = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 6),
            Text = $"鱼鱼现在记得 {memory?.Count ?? 0} 段对话的印象。聊得越重要、被提起得越多的事，记得越久。" };
        page.Children.Add(memoryInfo);
        var forget = BigBlueFish.Deployment.ModernUI.Button("让鱼鱼忘掉所有聊天记忆", () =>
        {
            if (BigBlueFish.Deployment.ModernUI.Choice(companionSettings, "忘掉聊天记忆", "鱼鱼会忘掉以前聊过的所有内容（好感度和称呼会保留）。确定吗？", "忘掉吧", "再想想") != 0) return;
            memory?.Clear();
            chatSession.Clear();
            memoryInfo.Text = "鱼鱼现在记得 0 段对话的印象。";
        });
        forget.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(forget);

        // ---- care
        Section("关怀提醒");
        ToggleWithMinutes("久坐提醒：连续使用电脑", preferences.SitReminder, v => preferences.SitReminder = v,
            [30, 45, 50, 60, 90], preferences.SitMinutes, m => { preferences.SitMinutes = m; sitActiveSeconds = 0; });
        ToggleWithMinutes("喝水提醒：每隔", preferences.WaterReminder, v => preferences.WaterReminder = v,
            [30, 45, 60, 90, 120], preferences.WaterMinutes, m => { preferences.WaterMinutes = m; waterActiveSeconds = 0; });
        ToggleWithMinutes("护眼提醒：每隔", preferences.EyeReminder, v => preferences.EyeReminder = v,
            [20, 30, 45, 60], preferences.EyeMinutes, m => { preferences.EyeMinutes = m; eyeActiveSeconds = 0; });
        Toggle("深夜（23:30 以后）还在用电脑时，催主人睡觉", preferences.BedtimeReminder, v => preferences.BedtimeReminder = v);
        Toggle("离开电脑 10 分钟以上回来时，迎接主人", preferences.WelcomeBack, v => preferences.WelcomeBack = v);
        Note("离开电脑 5 分钟以上会自动算作休息，久坐和护眼计时重新开始；专注期间提醒会等专注结束。");

        // ---- focus
        Section("番茄钟");
        var focusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        focusRow.Children.Add(new TextBlock { Text = "专注", VerticalAlignment = VerticalAlignment.Center });
        focusRow.Children.Add(Minutes([15, 20, 25, 30, 45, 50, 60], preferences.FocusMinutes, m => preferences.FocusMinutes = m));
        focusRow.Children.Add(new TextBlock { Text = "休息", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) });
        focusRow.Children.Add(Minutes([3, 5, 10, 15], preferences.BreakMinutes, m => preferences.BreakMinutes = m));
        page.Children.Add(focusRow);
        Note("右键鱼鱼「专注」开始。专注时鱼鱼安静陪着，不爬行、不说话；结束时会叫你休息。");

        // ---- games
        Section("游戏陪玩");
        Toggle("玩游戏时陪着主人：全屏也不躲起来、偶尔加油", preferences.GameCompanion, v =>
        {
            preferences.GameCompanion = v;
            UpdateCompanionVisibility();
        });
#if COMPANION_OCR
        Toggle("识别其他游戏里的「胜利 / 失败」画面并做出反应（实验：每 15 秒在本机截图识别文字，可能略微影响帧率）",
            preferences.GameScreenReactions, v => preferences.GameScreenReactions = v);
#endif
        page.Children.Add(new TextBlock { Text = "额外的游戏（进程名或安装目录关键字，用逗号分隔）", Margin = new Thickness(0, 8, 0, 4) });
        var extra = new TextBox { Text = preferences.ExtraGames, MinHeight = 28, Padding = new Thickness(4, 3, 4, 3) };
        extra.LostFocus += (_, _) => { preferences.ExtraGames = extra.Text.Length > 500 ? extra.Text[..500] : extra.Text; SaveCompanion(); };
        page.Children.Add(extra);
        Note((gameActive ? $"正在陪主人玩《{gameName}》。" : "已内置 Steam、Epic、WeGame、米哈游、暴雪、Riot 等平台的常见游戏；游戏中鱼鱼不会调用模型，避免占用显卡。")
            + "游戏在前台时，鼠标点击总会穿过鱼鱼和她的气泡，不会挡住操作（切到游戏立即生效）；游戏中的提醒气泡 15 秒后自动收起。");

        // ---- Delta Force
        AddDeltaForceSettings(page, Section, Note, (label, value, change) => Toggle(label, value, change));
    }
}
