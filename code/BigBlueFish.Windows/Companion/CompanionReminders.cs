using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BigBlueFish.Windows;

internal sealed class CompanionReminder
{
    public DateTime DueUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Text { get; set; } = "";
}

// Understands everyday Chinese time phrases: "30分钟后开会", "一个半小时后", "下午3点半交报告",
// "明天早上8:30叫我起床", "15:30 喝药".
internal static class CompanionReminderParser
{
    private const string Number = @"[0-9]+(?:\.[0-9]+)?|[零〇一二两三四五六七八九十百]+";
    internal static readonly Regex Relative = new(
        @"(?<num>" + Number + @")?\s*个?\s*(?<half>半)?\s*(?<unit>秒钟?|分钟|分|个?小时|个?钟头|天)\s*(?:之后|以后|后)");
    internal static readonly Regex Absolute = new(
        @"(?<day>今天|明天|后天|今晚|明早|明晚)?\s*(?<period>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里)?\s*(?<hour>[0-9]{1,2}|[零一二两三四五六七八九十]{1,3})\s*(?:[:：]\s*(?<min>[0-9]{1,2})|点\s*(?:(?<half>半)|(?<quarter>一刻|三刻)|(?<min2>[0-9]{1,2}|[零一二三四五六七八九十]{1,3})\s*分?)?|时)");
    private static readonly Regex Triggers = new(@"提醒一下|提醒我|提醒|叫我|记得|帮我|请|鱼鱼|设个闹钟|定个闹钟|闹钟|到时候|的时候|一下");
    private static readonly Regex Request = new(@"提醒|叫我|闹钟");

    internal static bool LooksLikeRequest(string text) => Request.IsMatch(text);

    internal static double? ParseNumber(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        int total = 0, current = 0;
        foreach (char c in text)
        {
            int digit = "零〇一二两三四五六七八九".IndexOf(c);
            if (digit >= 0) { current = c switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, _ => 9 }; continue; }
            if (c == '十') { total += (current == 0 ? 1 : current) * 10; current = 0; continue; }
            if (c == '百') { total += (current == 0 ? 1 : current) * 100; current = 0; continue; }
            return null;
        }
        return total + current;
    }

    // Returns the due local time and the cleaned reminder text, or null when no time is found.
    internal static (DateTime Due, string Text)? Parse(string input, DateTime now)
    {
        string text = input.Trim().Replace('：', ':');
        DateTime? due = null;
        Match used = Match.Empty;
        var relative = Relative.Match(text);
        if (relative.Success && (relative.Groups["num"].Success || relative.Groups["half"].Success))
        {
            double amount = relative.Groups["num"].Success ? ParseNumber(relative.Groups["num"].Value) ?? -1 : 0;
            if (amount >= 0)
            {
                if (relative.Groups["half"].Success) amount += .5;
                string unit = relative.Groups["unit"].Value;
                TimeSpan span = unit.StartsWith("秒") ? TimeSpan.FromSeconds(amount)
                    : unit.StartsWith("分") ? TimeSpan.FromMinutes(amount)
                    : unit == "天" ? TimeSpan.FromDays(amount) : TimeSpan.FromHours(amount);
                if (span >= TimeSpan.FromSeconds(10) && span <= TimeSpan.FromDays(30)) { due = now + span; used = relative; }
            }
        }
        if (due == null)
        {
            var absolute = Absolute.Match(text);
            if (absolute.Success && ParseNumber(absolute.Groups["hour"].Value) is double hourValue)
            {
                int hour = (int)hourValue, minute = 0;
                if (absolute.Groups["min"].Success) minute = int.Parse(absolute.Groups["min"].Value, CultureInfo.InvariantCulture);
                else if (absolute.Groups["min2"].Success) minute = (int)(ParseNumber(absolute.Groups["min2"].Value) ?? -1);
                else if (absolute.Groups["half"].Success) minute = 30;
                else if (absolute.Groups["quarter"].Success) minute = absolute.Groups["quarter"].Value == "一刻" ? 15 : 45;
                string day = absolute.Groups["day"].Value, period = absolute.Groups["period"].Value;
                if (day is "今晚" or "明晚" && period.Length == 0) period = "晚上";
                if (day == "明早" && period.Length == 0) period = "早上";
                if (period is "晚上" or "夜里" && hour == 12) hour = 24; // 晚上12点 = midnight
                else if (period is "下午" or "傍晚" or "晚上" or "夜里" && hour < 12) hour += 12;
                else if (period == "中午" && hour < 6) hour += 12;
                else if (period == "凌晨" && hour == 12) hour = 0;
                if (hour >= 0 && hour <= 24 && minute >= 0 && minute <= 59)
                {
                    int addDays = day switch { "明天" or "明早" or "明晚" => 1, "后天" => 2, _ => 0 };
                    var candidate = now.Date.AddDays(addDays).AddHours(hour).AddMinutes(minute);
                    if (addDays == 0 && candidate <= now)
                    {
                        if (period.Length == 0 && hour < 12 && candidate.AddHours(12) > now) candidate = candidate.AddHours(12);
                        else candidate = candidate.AddDays(1);
                    }
                    due = candidate;
                    used = absolute;
                }
            }
        }
        if (due == null) return null;
        string rest = text.Remove(used.Index, used.Length);
        rest = Triggers.Replace(rest, " ");
        rest = Regex.Replace(rest, @"^[\s,，。.!！~～:、在要去]+|[\s,，。.!！~～:、]+$", "");
        rest = Regex.Replace(rest, @"\s+", " ");
        if (rest.Length == 0) rest = "时间到啦";
        if (rest.Length > 40) rest = rest[..40];
        return (due.Value, rest);
    }
}

public partial class MainWindow
{
    private const string RemindersFile = "reminders.json";
    private List<CompanionReminder> reminders = new();
    private readonly HashSet<CompanionReminder> missedReminders = new();

    private void LoadReminders()
    {
        try
        {
            if (File.Exists(RemindersFile))
                reminders = JsonSerializer.Deserialize<List<CompanionReminder>>(File.ReadAllText(RemindersFile)) ?? new();
        }
        catch { reminders = new(); }
        var now = DateTime.UtcNow;
        reminders.RemoveAll(r => string.IsNullOrWhiteSpace(r.Text) || (now - r.DueUtc).TotalHours > 12);
        foreach (var r in reminders.Where(r => r.DueUtc <= now)) missedReminders.Add(r);
    }

    private void SaveReminders()
    {
        try
        {
            File.WriteAllText(RemindersFile + ".tmp", JsonSerializer.Serialize(reminders));
            File.Move(RemindersFile + ".tmp", RemindersFile, true);
        }
        catch { }
    }

    private CompanionReminder AddReminder(DateTime dueLocal, string text)
    {
        var reminder = new CompanionReminder { DueUtc = dueLocal.ToUniversalTime(), CreatedUtc = DateTime.UtcNow, Text = text };
        reminders.Add(reminder);
        reminders.Sort((a, b) => a.DueUtc.CompareTo(b.DueUtc));
        SaveReminders();
        return reminder;
    }

    private static string DescribeDue(DateTime dueLocal)
    {
        var today = DateTime.Today;
        string day = dueLocal.Date == today ? "" : dueLocal.Date == today.AddDays(1) ? "明天" : dueLocal.Date == today.AddDays(2) ? "后天" : dueLocal.ToString("M月d日 ", CultureInfo.InvariantCulture);
        return day + dueLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void CheckReminders(DateTime nowUtc)
    {
        var due = reminders.Where(r => r.DueUtc <= nowUtc).ToList();
        if (due.Count == 0 || chatBusy) return; // a streaming chat reply would cover it; try again next second
        foreach (var r in due) reminders.Remove(r);
        SaveReminders();
        bool missed = due.All(r => missedReminders.Contains(r) || (nowUtc - r.DueUtc).TotalMinutes > 3);
        string items = string.Join("；", due.Select(r => $"{DescribeDue(r.DueUtc.ToLocalTime())} {r.Text}"));
        string line = missed ? $"{bond.Address}，有提醒过时了（{items}），别忘了哦～" : $"{bond.Address}！到时间啦：{items}～";
        foreach (var r in due) missedReminders.Remove(r);
        if (PuppetActive) { notifyIcon?.ShowBalloonTip(10000, "蓝色大肥鱼 · 提醒", line, System.Windows.Forms.ToolTipIcon.Info); return; }
        if (crawl is { Finished: false } walking) walking.StopRequested = true;
        Say(line, "点击回应-元气挥手", true);
        if (GameActive || fullscreenHidden) notifyIcon?.ShowBalloonTip(10000, "蓝色大肥鱼 · 提醒", line, System.Windows.Forms.ToolTipIcon.Info);
    }

    // Shared by the chat box and the reminder panel. Returns a confirmation line, or null.
    private string? TryCreateReminder(string text)
    {
        var parsed = CompanionReminderParser.Parse(text, DateTime.Now);
        if (parsed == null) return null;
        var (due, what) = parsed.Value;
        AddReminder(due, what);
        return $"好哒！鱼鱼记住啦，{DescribeDue(due)} 提醒{bond.Address}：{what}～";
    }

    private void ShowReminderPanel()
    {
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var body = new StackPanel();
        var input = new TextBox { FontSize = 13, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), Foreground = blue };
        System.Windows.Automation.AutomationProperties.SetName(input, "提醒内容");
        var placeholder = new TextBlock { Text = "例如：30分钟后 开会 / 下午3点半 交报告", Foreground = Brushes.Gray, FontSize = 12,
            Margin = new Thickness(9, 7, 0, 0), IsHitTestVisible = false };
        var editor = new Grid();
        editor.Children.Add(input);
        editor.Children.Add(placeholder);
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var status = new TextBlock { Foreground = Brushes.SlateGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        var list = new StackPanel();
        void Refresh()
        {
            list.Children.Clear();
            if (reminders.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "暂时没有待提醒的事项", Foreground = Brushes.SlateGray, FontSize = 12 });
                return;
            }
            foreach (var reminder in reminders.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = $"{DescribeDue(reminder.DueUtc.ToLocalTime())}  {reminder.Text}", Foreground = blue,
                    FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var cancel = new Button { Content = "取消", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, MinWidth = 0, Margin = new Thickness(6, 0, 0, 0) };
                cancel.Click += (_, _) => { reminders.Remove(reminder); SaveReminders(); Refresh(); };
                Grid.SetColumn(cancel, 1);
                row.Children.Add(cancel);
                list.Children.Add(row);
            }
        }
        void Add()
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            var confirmation = TryCreateReminder(input.Text);
            if (confirmation == null) { status.Text = "没看懂时间呢……试试「20分钟后 喝水」或「15:30 开会」"; return; }
            status.Text = confirmation;
            input.Clear();
            Refresh();
        }
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Add(); } };
        var add = BigBlueFish.Deployment.ModernUI.Button("添加提醒", Add, true);
        add.Margin = new Thickness(0, 6, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(editor);
        body.Children.Add(add);
        body.Children.Add(status);
        body.Children.Add(new TextBlock { Text = "待提醒", Foreground = blue, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 2, 0, 4) });
        body.Children.Add(list);
        Refresh();
        ShowPetPanel("让鱼鱼提醒你", body, 330, false);
        input.Focus();
    }
}
