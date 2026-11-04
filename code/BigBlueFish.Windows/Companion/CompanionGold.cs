using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BigBlueFish.Windows;

internal sealed record GoldQuote(string Variety, string Code, double Price, double UpDown, double Open, double PreClose,
    double High, double Low, string Time)
{
    internal double Percent => PreClose > 0 ? UpDown / PreClose * 100 : 0;
    internal string Unit => Code.StartsWith("AG", StringComparison.Ordinal) ? "元/千克" : "元/克";
}

// 招商银行一网通「黄金市场行情」(https://m.cmbchina.com/goldrate.html): Shanghai Gold Exchange quotes as shown by CMB.
internal static class CompanionGoldFeed
{
    internal const string Url = "https://m.cmbchina.com/api/rate/gold";
    internal static readonly (string Code, string Name)[] Varieties =
    [
        ("AU9999", "Au99.99（黄金）"), ("AUTD", "Au(T+D)（黄金延期）"), ("AU100G", "Au100g（100 克金条）"), ("AU9995", "Au99.95"),
        ("AGTD", "Ag(T+D)（白银延期）"), ("PT9995", "Pt99.95（铂金）")
    ];

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // Quotes with a real price; the date of the snapshot ("2026-11-02 10:34") comes out separately.
    internal static List<GoldQuote> Parse(string json, out string snapshot)
    {
        snapshot = "";
        var result = new List<GoldQuote>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Text(root, "returnCode") != "SUC0000" || !root.TryGetProperty("body", out var body)) return result;
        snapshot = Text(body, "time");
        if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var quote = new GoldQuote(Text(item, "variety"), Text(item, "goldNo"), Number(item, "curPrice"), Number(item, "upDown"),
                Number(item, "open"), Number(item, "preClose"), Number(item, "high"), Number(item, "low"), Text(item, "time"));
            if (quote.Code.Length > 0 && quote.Price > 0) result.Add(quote);
        }
        return result;
    }

    // Shanghai Gold Exchange sessions: 9:00–11:30, 13:30–15:30 and 20:00–2:30 (night session runs into the next morning).
    internal static bool MarketOpen(DateTime local)
    {
        var t = local.TimeOfDay;
        bool weekday = local.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        bool afterNight = local.DayOfWeek is >= DayOfWeek.Tuesday and <= DayOfWeek.Saturday && t < new TimeSpan(2, 30, 0);
        bool day = weekday && (t >= new TimeSpan(9, 0, 0) && t <= new TimeSpan(11, 30, 0) || t >= new TimeSpan(13, 30, 0) && t <= new TimeSpan(15, 30, 0));
        bool night = weekday && t >= new TimeSpan(20, 0, 0);
        return day || night || afterNight;
    }
}

// Real-time CMB gold price: polls the quote, records every change in data\gold-records.csv, tells the owner about
// big moves and about the price levels she was asked to watch, and answers "金价多少" in chat.
public partial class MainWindow
{
    private const string GoldRecordFile = "gold-records.csv";
    private readonly HttpClient goldWeb = new() { Timeout = TimeSpan.FromSeconds(10) };
    private GoldQuote? goldQuote;
    private string goldSnapshot = "", goldStatus = "还没有获取金价。";
    private DateTime nextGoldFetch = DateTime.MinValue, goldDay = DateTime.MinValue;
    private bool goldFetching, goldHighArmed = true, goldLowArmed = true;
    private int goldFailures;
    private double goldAnnounced = double.NaN, goldLastRecorded = double.NaN;
    private readonly List<(DateTime Local, double Price)> goldToday = new();
    private readonly List<string> goldChanges = new(); // newest last, for the settings page

    private string GoldName => CompanionGoldFeed.Varieties.FirstOrDefault(v => v.Code == preferences.GoldVariety).Name ?? preferences.GoldVariety;

    private void InitializeGold()
    {
        if (!goldWeb.DefaultRequestHeaders.UserAgent.Any())
            goldWeb.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BigBlueFish");
        LoadGoldToday();
        // Automated UI tests run offline.
        goldOffline = App.Args.Contains("--test-mode");
        nextGoldFetch = goldOffline ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(8);
        nextGoldHistoryTry = goldOffline ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(20);
    }

    // CareTick, every second.
    private void UpdateGold(DateTime now)
    {
        UpdateGoldHistory(now);
        if (!preferences.GoldWatch || goldFetching || now < nextGoldFetch) return;
        bool open = CompanionGoldFeed.MarketOpen(DateTime.Now);
        // Outside trading hours the quote does not move: look far less often.
        nextGoldFetch = now.AddSeconds(goldFailures >= 3 ? 300 : open ? preferences.GoldIntervalSeconds : 600);
        _ = FetchGold();
    }

    private async Task FetchGold(bool announce = true)
    {
        if (goldOffline) return;
        goldFetching = true;
        try
        {
            string json = await goldWeb.GetStringAsync(CompanionGoldFeed.Url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var quotes = CompanionGoldFeed.Parse(json, out var snapshot);
            if (quotes.Count > 0) goldAllQuotes = quotes;
            var quote = quotes.FirstOrDefault(q => q.Code == preferences.GoldVariety);
            if (quote == null) { goldStatus = $"{DateTime.Now:HH:mm:ss} 行情里暂时没有 {GoldName} 的报价。"; return; }
            goldFailures = 0;
            goldSnapshot = snapshot;
            goldStatus = $"{DateTime.Now:HH:mm:ss} 已更新（行情时间 {(snapshot.Length > 0 ? snapshot : quote.Time)}）";
            OnGoldQuote(quote, announce);
        }
        catch (Exception ex)
        {
            goldFailures++;
            goldStatus = $"{DateTime.Now:HH:mm:ss} 获取失败：{(ex is TaskCanceledException ? "连接超时" : ex.Message)}";
        }
        finally { goldFetching = false; }
    }

    private void OnGoldQuote(GoldQuote quote, bool announce)
    {
        var local = DateTime.Now;
        if (goldDay != local.Date) { goldDay = local.Date; goldToday.Clear(); }
        bool changed = goldQuote == null || goldQuote.Price != quote.Price || goldQuote.Code != quote.Code;
        goldQuote = quote;
        if (changed) NotifyGoldViews(); // the forecast's middle and the panel follow the live price
        if (!changed && !double.IsNaN(goldLastRecorded)) return;
        if (quote.Price != goldLastRecorded) RecordGold(quote, local);
        if (!announce) return;

        // Watched levels: one alert each time the price crosses them.
        if (preferences.GoldAlertHigh > 0)
        {
            if (goldHighArmed && quote.Price >= preferences.GoldAlertHigh)
            {
                goldHighArmed = false;
                Say($"{bond.Address}！招行{quote.Variety}涨到 {quote.Price:0.00} {quote.Unit} 啦，到你设的 {preferences.GoldAlertHigh:0.##} 了！",
                    "余额-钱袋满溢", true);
                goldAnnounced = quote.Price;
                return;
            }
            if (quote.Price < preferences.GoldAlertHigh - 1) goldHighArmed = true;
        }
        if (preferences.GoldAlertLow > 0)
        {
            if (goldLowArmed && quote.Price <= preferences.GoldAlertLow)
            {
                goldLowArmed = false;
                Say($"{bond.Address}！招行{quote.Variety}跌到 {quote.Price:0.00} {quote.Unit} 了，低于你设的 {preferences.GoldAlertLow:0.##}！",
                    "余额-数金皱眉", true);
                goldAnnounced = quote.Price;
                return;
            }
            if (quote.Price > preferences.GoldAlertLow + 1) goldLowArmed = true;
        }

        // Big moves since she last mentioned the price.
        if (double.IsNaN(goldAnnounced)) { goldAnnounced = quote.Price; return; }
        double move = quote.Price - goldAnnounced;
        if (!preferences.GoldAnnounce || Math.Abs(move) < preferences.GoldStep || !CanChatter || GameActive) return;
        goldAnnounced = quote.Price;
        string direction = move > 0 ? "涨" : "跌";
        Say($"招行金价{direction}了 {Math.Abs(move):0.##}！{quote.Variety} 现在 {quote.Price:0.00} {quote.Unit}，今天 {quote.UpDown:+0.00;-0.00}（{quote.Percent:+0.00;-0.00}%）",
            move > 0 ? "余额-金袋叮当" : "余额-数金皱眉");
    }

    private void RecordGold(GoldQuote quote, DateTime local)
    {
        double previous = goldLastRecorded;
        goldLastRecorded = quote.Price;
        goldToday.Add((local, quote.Price));
        if (goldToday.Count > 3000) goldToday.RemoveRange(0, goldToday.Count - 3000);
        string delta = double.IsNaN(previous) ? "" : $"（{quote.Price - previous:+0.00;-0.00}）";
        goldChanges.Add($"{local:HH:mm:ss}  {quote.Price:0.00}{delta}");
        if (goldChanges.Count > 50) goldChanges.RemoveRange(0, goldChanges.Count - 50);
        try
        {
            var info = new FileInfo(GoldRecordFile);
            if (info.Exists && info.Length > 5 * 1024 * 1024) File.Move(GoldRecordFile, "gold-records.old.csv", true);
            if (!File.Exists(GoldRecordFile))
                File.WriteAllText(GoldRecordFile, "记录时间,行情时间,品种,最新价,涨跌,涨跌幅%,开盘,昨收,最高,最低\r\n", new UTF8Encoding(true));
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            File.AppendAllText(GoldRecordFile, string.Join(",", local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                goldSnapshot.Length > 0 ? goldSnapshot : quote.Time, quote.Variety, F(quote.Price), F(quote.UpDown),
                quote.Percent.ToString("0.00", CultureInfo.InvariantCulture), F(quote.Open), F(quote.PreClose), F(quote.High), F(quote.Low)) + "\r\n",
                Encoding.UTF8);
        }
        catch { } // the record is a convenience; the live price still works
    }

    // Today's earlier records (for the chart and the change list) after a restart.
    private void LoadGoldToday()
    {
        goldDay = DateTime.Now.Date;
        goldToday.Clear();
        goldChanges.Clear();
        try
        {
            if (!File.Exists(GoldRecordFile)) return;
            string variety = CompanionGoldFeed.Varieties.FirstOrDefault(v => v.Code == preferences.GoldVariety).Name?.Split('（')[0] ?? "";
            double previous = double.NaN;
            foreach (var line in File.ReadLines(GoldRecordFile, Encoding.UTF8).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 4 || parts[2] != variety) continue;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    || time.Date != goldDay || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var price)) continue;
                goldToday.Add((time, price));
                goldChanges.Add($"{time:HH:mm:ss}  {price:0.00}" + (double.IsNaN(previous) ? "" : $"（{price - previous:+0.00;-0.00}）"));
                previous = price;
            }
            if (goldChanges.Count > 50) goldChanges.RemoveRange(0, goldChanges.Count - 50);
            if (!double.IsNaN(previous)) goldLastRecorded = previous;
        }
        catch { }
    }

    private string GoldSummary()
    {
        if (goldQuote is not { } q) return preferences.GoldWatch ? "鱼鱼还在去招行看金价的路上，等一下下～" : "金价监测在设置里关掉了哦（设置 → 互动与关怀 → 招商银行金价）。";
        var text = new StringBuilder($"招行 {q.Variety}：{q.Price:0.00} {q.Unit}，今天 {q.UpDown:+0.00;-0.00}（{q.Percent:+0.00;-0.00}%），最高 {q.High:0.00}、最低 {q.Low:0.00}");
        if (goldToday.Count >= 2)
        {
            var first = goldToday[0];
            text.Append($"；从 {first.Local:HH:mm} 记到现在 {q.Price - first.Price:+0.00;-0.00}");
        }
        text.Append(CompanionGoldFeed.MarketOpen(DateTime.Now) ? "。" : "（现在休市，是最近的收盘价）。");
        return text.ToString();
    }

    private static readonly Regex GoldAsk = new(@"(?<![基租押奖现资薪定佣酬])金价|黄金.{0,6}(价|多少|行情|涨|跌|走势|趋势|预测|推测|前景)|金子.{0,4}(价|涨|跌|走势)"
        + @"|[Aa][Uu]\s?99|招行.{0,3}(黄金|白银)|(白银|银价|铂金|铂价).{0,6}(多少|行情|涨|跌|走势|趋势|预测|推测|前景)"
        + @"|(买|卖|入手|抄底|囤|投资).{0,4}(黄金|金条|金子|白银|银条)|(黄金|金条|金子|白银|银条).{0,4}(买|卖|入手|抄底|投资)");
    // Holidays, prime time and game ranks ("黄金段位跌回白银") are not about the metal.
    private static readonly Regex GoldNotAbout = new("黄金周|黄金档|黄金时|黄金比例|黄金分割|黄金搭档|黄金矿工|段位|上分|掉分|掉段|排位|晋级|王者|星耀|钻石|青铜|宗师|跌回|打回");

    // Chat: "金价多少" is answered with the live quote, "金价明天会涨吗" with the forecast.
    private bool TryGoldChat(string message)
    {
        if (!GoldAsk.IsMatch(message) || GoldNotAbout.IsMatch(message)) return false;
        if (GoldOtherMetal(message) is { } other) { ChatReply(message, other, remember: false); return true; }
        if (IsGoldForecastAsk(message) || GoldAdviceAsk.IsMatch(message)) { AnswerGoldForecast(message); return true; }
        if (preferences.GoldWatch && goldQuote == null && !goldFetching) _ = FetchGold(false);
        MaybeRefreshGoldFactors();
        ChatReply(message, GoldTodayText(), goldQuote is { UpDown: >= 0 } ? "余额-金袋叮当" : "余额-数金皱眉", remember: false);
        return true;
    }

    // ---------------------------------------------------------------- settings
    private void AddGoldSettings(StackPanel page, Action<string> section, Action<string> note, Func<string, bool, Action<bool>, CheckBox> toggle)
    {
        section("招商银行金价");
        toggle("实时监测招商银行黄金行情（数据来自招行一网通「黄金市场行情」，价格每次变化都记到本机 data\\gold-records.csv）",
            preferences.GoldWatch, v => { preferences.GoldWatch = v; if (v) nextGoldFetch = DateTime.UtcNow; });
        StackPanel Row() { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) }; page.Children.Add(row); return row; }
        TextBlock Caption(string text, double width = 90) => new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };
        ComboBox Choice<T>(IReadOnlyList<(T Value, string Name)> options, T current, Action<T> change)
        {
            var combo = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var (_, name) in options) combo.Items.Add(name);
            int index = options.Select((o, i) => (o, i)).FirstOrDefault(p => EqualityComparer<T>.Default.Equals(p.o.Value, current)).i;
            combo.SelectedIndex = Math.Max(0, index);
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { change(options[combo.SelectedIndex].Value); SaveCompanion(); } };
            return combo;
        }
        var first = Row();
        first.Children.Add(Caption("品种", 50));
        first.Children.Add(Choice(CompanionGoldFeed.Varieties.Select(v => (v.Code, v.Name)).ToList(), preferences.GoldVariety, code =>
        {
            preferences.GoldVariety = code;
            goldQuote = null;
            goldAnnounced = goldLastRecorded = double.NaN;
            LoadGoldToday();
            nextGoldFetch = DateTime.UtcNow;
            nextGoldHistoryTry = DateTime.MinValue;
            NotifyGoldViews();
            if (preferences.GoldWatch) _ = RefreshGoldHistory(false);
        }));
        first.Children.Add(Caption("刷新", 40));
        first.Children.Add(Choice(new List<(int, string)> { (30, "每 30 秒"), (60, "每 1 分钟"), (120, "每 2 分钟"), (300, "每 5 分钟") },
            preferences.GoldIntervalSeconds, s => preferences.GoldIntervalSeconds = s));
        var second = Row();
        second.Children.Add(new CheckBox { Content = "涨跌超过", IsChecked = preferences.GoldAnnounce, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        ((CheckBox)second.Children[0]).Click += (_, e) => { preferences.GoldAnnounce = ((CheckBox)e.Source).IsChecked == true; SaveCompanion(); };
        second.Children.Add(Choice(new List<(double, string)> { (1, "1 元"), (2, "2 元"), (3, "3 元"), (5, "5 元"), (8, "8 元"), (10, "10 元"), (20, "20 元") },
            preferences.GoldStep, s => preferences.GoldStep = s));
        second.Children.Add(Caption("时鱼鱼播报（玩游戏、专注时不打扰）", 260));
        var third = Row();
        TextBox Level(double value, Action<double> change)
        {
            var box = new TextBox { Width = 90, Text = value > 0 ? value.ToString("0.##", CultureInfo.InvariantCulture) : "", Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
            string shown = box.Text;
            box.LostFocus += (_, _) =>
            {
                // Only an edited box writes (the gold panel edits the same prices), and only a new price re-arms the alerts.
                if (box.Text.Trim() == shown) return;
                double parsed = double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && v < 1_000_000 ? v : 0;
                box.Text = shown = parsed > 0 ? parsed.ToString("0.##", CultureInfo.InvariantCulture) : "";
                if (parsed == value) return;
                value = parsed;
                change(parsed);
                goldHighArmed = goldLowArmed = true;
                SaveCompanion();
                goldPanelView?.Invoke();
            };
            return box;
        }
        third.Children.Add(Caption("计划卖出价（高于时提醒）", 170));
        third.Children.Add(Level(preferences.GoldAlertHigh, v => preferences.GoldAlertHigh = v));
        third.Children.Add(Caption("计划买入价（低于时提醒）", 170));
        third.Children.Add(Level(preferences.GoldAlertLow, v => preferences.GoldAlertLow = v));
        page.Children.Add(new TextBlock { Text = "留空 = 不提醒。金价面板里也能改，并能看到离现价多远、按波动粗算的触及可能性。", FontSize = 11,
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        var summary = new TextBlock { Text = GoldSummary(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
        page.Children.Add(summary);
        page.Children.Add(GoldChart());
        var changes = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
            Text = goldChanges.Count == 0 ? "今天还没有记录到变化。" : "最近的变化：" + string.Join("　", goldChanges.TakeLast(8).Reverse()) };
        page.Children.Add(changes);
        var buttons = Row();
        var refresh = BigBlueFish.Deployment.ModernUI.Button("立即刷新", async () =>
        {
            if (goldFetching) return;
            await FetchGold(false);
            summary.Text = GoldSummary();
            changes.Text = goldChanges.Count == 0 ? "今天还没有记录到变化。" : "最近的变化：" + string.Join("　", goldChanges.TakeLast(8).Reverse());
        });
        refresh.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(refresh);
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button("打开记录文件", () =>
        {
            try
            {
                var path = System.IO.Path.GetFullPath(GoldRecordFile);
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{System.IO.Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            }
            catch { }
        }));
        AddGoldForecastSettings(page);
        note(goldStatus + " 交易时段（9:00–11:30、13:30–15:30、20:00–次日 2:30）按设定间隔刷新，休市时每 10 分钟看一次。右键鱼鱼「招行金价」，她用气泡告诉你现价和今天的预计区间；「金价面板…」用表格看金价变化、预测、买卖参考和判断依据；聊天时问「金价多少」「明天金价会涨吗」「黄金下个月走势」也行。");
    }

    // Today's price line (records since midnight), drawn small under the summary.
    private FrameworkElement GoldChart()
    {
        const double width = 420, height = 90;
        var canvas = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(248, 249, 252)) };
        var frame = new Border { Child = canvas, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 235)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        if (goldToday.Count < 2)
        {
            canvas.Children.Add(new TextBlock { Text = "今天的走势会画在这里（至少需要两条记录）", Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(8, 34, 0, 0) });
            return frame;
        }
        double min = goldToday.Min(p => p.Price), max = goldToday.Max(p => p.Price);
        if (max - min < 0.01) { max += 0.5; min -= 0.5; }
        var start = goldToday[0].Local; var end = goldToday[^1].Local;
        double span = Math.Max(1, (end - start).TotalSeconds);
        var line = new Polyline { StrokeThickness = 1.6, Stroke = goldToday[^1].Price >= goldToday[0].Price ? new SolidColorBrush(Color.FromRgb(214, 64, 64)) : new SolidColorBrush(Color.FromRgb(40, 150, 90)) };
        foreach (var (time, price) in goldToday)
            line.Points.Add(new Point(6 + (time - start).TotalSeconds / span * (width - 12), 8 + (max - price) / (max - min) * (height - 16)));
        canvas.Children.Add(line);
        var labels = new TextBlock { Text = $"{max:0.00}", FontSize = 10, Foreground = Brushes.SlateGray };
        Canvas.SetLeft(labels, 4); Canvas.SetTop(labels, 0);
        canvas.Children.Add(labels);
        var low = new TextBlock { Text = $"{min:0.00}　{start:HH:mm}–{end:HH:mm}", FontSize = 10, Foreground = Brushes.SlateGray };
        Canvas.SetLeft(low, 4); Canvas.SetTop(low, height - 14);
        canvas.Children.Add(low);
        return frame;
    }
}
