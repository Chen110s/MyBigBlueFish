using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// 金价推测 (天 / 周 / 月 / 年): daily history of the same SGE contract (cached in data\gold-history-*.csv), the range model in
// CompanionGoldForecast, the first forecast of each trading day kept in data\gold-forecasts.csv and checked when it comes due.
public partial class MainWindow
{
    private const string GoldForecastFile = "gold-forecasts.csv";
    private static readonly TimeSpan GoldHistoryAge = TimeSpan.FromHours(3);
    private HttpClient? goldHistoryWeb;
    private List<GoldBar> goldBars = new();
    private string goldBarsCode = "", goldHistoryStatus = "还没有读取历史日线。";
    private DateTime goldBarsFetched = DateTime.MinValue, nextGoldHistoryTry = DateTime.MinValue;
    private Task? goldHistoryTask;
    private GoldForecast? goldForecast;
    private List<GoldForecastRecord>? goldForecastRecords;
    private Action? goldForecastView; // refreshes the open settings table
    private bool goldOffline; // automated UI tests
    private bool goldHistoryLoading;

    private static string GoldHistoryFile(string historyCode) => $"gold-history-{historyCode}.csv";

    private HttpClient GoldHistoryWeb()
    {
        if (goldHistoryWeb != null) return goldHistoryWeb;
        var web = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        web.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BigBlueFish");
        web.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        return goldHistoryWeb = web;
    }

    // The cached bars of the chosen variety (switching variety switches the cache).
    private void LoadGoldBars()
    {
        string history = CompanionGoldForecast.HistoryCode(preferences.GoldVariety);
        if (goldBarsCode == history) return;
        goldBarsCode = history;
        goldBars = CompanionGoldForecast.ReadCache(GoldHistoryFile(history));
        goldBarsFetched = DateTime.MinValue;
        goldForecast = null;
    }

    private bool GoldHistoryStale => DateTime.UtcNow - goldBarsFetched > GoldHistoryAge;

    // CareTick (through UpdateGold): keeps the history fresh while gold watching is on; retries every 15 minutes on failure.
    private void UpdateGoldHistory(DateTime now)
    {
        if (!preferences.GoldWatch || now < nextGoldHistoryTry || goldHistoryTask is { IsCompleted: false }) return;
        LoadGoldBars();
        if (!GoldHistoryStale) return;
        nextGoldHistoryTry = now.AddMinutes(15);
        _ = RefreshGoldHistory(false);
    }

    // Menu, chat and settings: top up a stale history, but respect the 15-minute pause after a failure.
    private void MaybeRefreshGoldHistory()
    {
        if (!goldOffline && preferences.GoldWatch && GoldHistoryStale && DateTime.UtcNow >= nextGoldHistoryTry) _ = RefreshGoldHistory(false);
    }

    // One download at a time; callers may await the one already running.
    private Task RefreshGoldHistory(bool force) =>
        goldHistoryTask is { IsCompleted: false } running ? running : goldHistoryTask = RefreshGoldHistoryCore(force);

    private async Task RefreshGoldHistoryCore(bool force)
    {
        LoadGoldBars();
        string history = goldBarsCode;
        if (!goldOffline && (force || GoldHistoryStale))
        {
            goldHistoryLoading = true;
            try
            {
                // With a long cache only the last few weeks are asked for.
                DateTime? since = goldBars.Count >= 500 ? goldBars[^1].Date.AddDays(-20) : null;
                string json = await GoldHistoryWeb().GetStringAsync(CompanionGoldForecast.HistoryUrl(history, since));
                var fresh = CompanionGoldForecast.ParseHistory(json);
                if (fresh.Count == 0) throw new InvalidDataException($"没有返回 {history} 的日线");
                var cached = goldBarsCode == history ? goldBars : CompanionGoldForecast.ReadCache(GoldHistoryFile(history));
                var merged = CompanionGoldForecast.Merge(cached, fresh);
                try { CompanionGoldForecast.WriteCache(GoldHistoryFile(history), merged); } catch { }
                if (goldBarsCode == history) { goldBars = merged; goldBarsFetched = DateTime.UtcNow; }
                goldHistoryStatus = $"{DateTime.Now:HH:mm} 历史日线已更新（{merged[0].Date:yyyy-MM-dd} 起共 {merged.Count} 个交易日）。";
            }
            catch (Exception ex)
            {
                nextGoldHistoryTry = DateTime.UtcNow.AddMinutes(15);
                goldHistoryStatus = $"{DateTime.Now:HH:mm} 历史日线获取失败：{(ex is TaskCanceledException ? "连接超时" : ex.Message)}"
                    + (goldBars.Count >= CompanionGoldForecast.MinimumDays ? "；先用本机缓存的日线推算。" : "。");
            }
            finally { goldHistoryLoading = false; }
        }
        // The variety was switched while this download ran: let the next tick fetch the new one right away.
        if (goldBarsCode != history) nextGoldHistoryTry = DateTime.MinValue;
        UpdateGoldForecast();
        goldForecastView?.Invoke();
    }

    // Cheap: recomputed whenever it is shown, so the middle estimate follows the live price.
    private void UpdateGoldForecast()
    {
        LoadGoldBars();
        string code = preferences.GoldVariety;
        bool live = goldQuote is { } quote && quote.Code == code;
        goldForecast = CompanionGoldForecast.Compute(code, goldBars, live ? goldQuote!.Price : 0, live, DateTime.Now);
        // Kept only when the last bar really is the current trading day (freshly downloaded) and the base price is current:
        // the live quote, or the close of a finished day. Au100g is priced off Au99.99 bars, so it is not scored.
        if (goldForecast is { } f && DateTime.UtcNow - goldBarsFetched < TimeSpan.FromHours(6)
            && (f.LiveBase || CompanionGoldForecast.Final(goldBars[^1], DateTime.Now)) && CompanionGoldForecast.HistoryCode(code) == code)
            RecordGoldForecast(f);
    }

    private List<GoldForecastRecord> GoldForecastRecords() => goldForecastRecords ??= CompanionGoldForecast.ReadRecords(GoldForecastFile);

    // The first forecast for each trading day is kept, so it can be compared with what really happened.
    private void RecordGoldForecast(GoldForecast forecast)
    {
        var records = GoldForecastRecords();
        if (records.Any(r => r.Code == forecast.Code && r.BaseDate == forecast.BaseDate)) return;
        var made = DateTime.Now;
        var fresh = forecast.Rows.Select(r => new GoldForecastRecord(made, forecast.Code, forecast.BaseDate, r.Days, forecast.Base,
            r.Center, r.Low68, r.High68, r.Low95, r.High95)).ToList();
        records.AddRange(fresh);
        try { CompanionGoldForecast.AppendRecords(GoldForecastFile, fresh); } catch { }
    }

    private string GoldForecastScoreText()
    {
        var scores = CompanionGoldForecast.Score(GoldForecastRecords(), preferences.GoldVariety, goldBars, DateTime.Now);
        var done = scores.Where(s => s.Checked > 0).ToList();
        int days = GoldForecastRecords().Where(r => r.Code == preferences.GoldVariety).Select(r => r.BaseDate).Distinct().Count();
        if (CompanionGoldForecast.HistoryCode(preferences.GoldVariety) != preferences.GoldVariety)
            return "Au100g 借用 Au99.99 的日线推算，不记录、不对答案。";
        if (done.Count == 0)
            return days == 0 ? "每个交易日第一次推测会记到 data\\gold-forecasts.csv，到期后自动对答案。"
                : $"已记下 {days} 天的推测，到期后自动对答案（下个交易日的那条最快明天收盘后就能对）。";
        string Name(int d) => CompanionGoldForecast.Horizons.First(h => h.Days == d).Label;
        return "对答案：" + string.Join("；", done.Select(s =>
            $"{Name(s.Days)} 已到期 {s.Checked} 次，{s.Inside68} 次落在 68% 区间、{s.Inside95} 次在 95% 区间，中间值平均差 {s.MeanMissPercent:0.0}%")) + "。";
    }

    private string GoldForecastName => GoldName.Split('（')[0];

    // Right-click 「招行金价」: the live quote plus a one-line forecast when the history is at hand.
    private string GoldMenuText()
    {
        string text = GoldSummary();
        if (!preferences.GoldWatch) return text;
        UpdateGoldForecast();
        MaybeRefreshGoldHistory();
        if (goldForecast is not { } f || f.Row(1) is not { } day || f.Row(21) is not { } month) return text;
        string P(double v) => CompanionGoldForecast.Price(v);
        return text + $"\n按历史波动，约 68% 的情况下：下个交易日 {P(day.Low68)}～{P(day.High68)}，一个月 {P(month.Low68)}～{P(month.High68)}"
            + "（统计推算，不构成投资建议；问我「金价预测」看更多）";
    }

    // Chat / bubble text: one horizon, or (days == null) all four.
    private string GoldForecastText(int? days, bool advice = false)
    {
        if (goldForecast is not { } f) return "鱼鱼还没拿到历史日线，没法推算……";
        string unit = CompanionGoldForecast.Unit(f.Code);
        string P(double v) => CompanionGoldForecast.Price(v);
        var text = new StringBuilder();
        if (advice) text.Append("买不买鱼鱼可不敢替你拿主意（鱼鱼不是理财顾问），不过可以给你看看统计上的区间：\n");
        string basis = f.LiveBase ? $"现在 {P(f.Base)} {unit}" : $"{f.BaseDate.Month} 月 {f.BaseDate.Day} 日收盘 {P(f.Base)} {unit}";
        if (days is int d && f.Row(d) is { } row)
        {
            string span = d == 1 ? "一个交易日" : row.Label;
            text.Append($"招行 {GoldForecastName} {basis}。按近 {Math.Round(f.Years):0} 年的历史波动推算，{row.Label}的收盘约 68% 的情况会落在 {P(row.Low68)}～{P(row.High68)}，")
                .Append($"约 95% 的情况在 {P(row.Low95)}～{P(row.High95)}。");
            if (double.IsFinite(row.UpShare)) text.Append($"历史上隔{span}价格比之前高的占 {row.UpShare * 100:0}%。");
            if (d == CompanionGoldForecast.YearDays) text.Append("一年这么长，变数很大，前两年金价大涨时这个区间就常常没接住，只能当个大概哦。");
        }
        else
        {
            text.Append($"招行 {GoldForecastName} 推测（{basis}；按历史波动，约 68% 的情况落在这些区间）：");
            foreach (var r in f.Rows) text.Append($"\n{r.Label}：{P(r.Low68)}～{P(r.High68)}");
        }
        text.Append('\n').Append(f.Trend).Append("只是统计推算，不是预言，也不构成投资建议～");
        return text.ToString();
    }

    // "金价明天会涨吗" / "黄金走势预测" ask for the forecast; "今天金价涨了吗" / "现在金价走势" stay with the live quote.
    private static readonly Regex GoldFutureAsk = new(
        "预测|推测|预估|预计|预判|前景|未来|将来|以后|接下来|明天|后天|下周|下个?星期|下个?礼拜|下个?月|明年|年底|年后|"
        + "会不会[涨跌]|会[涨跌]|能[涨跌]|还[会能要]?[涨跌]|[涨跌]还是[涨跌]");
    private static readonly Regex GoldMaybeAsk = new("走势|趋势|之后|[涨跌](吗|么|不)");
    private static readonly Regex GoldNowWord = new("今天|今日|现在|目前|此刻|刚才|刚刚");
    private static readonly Regex GoldAdviceAsk = new("买|卖|入手|抄底|出手|上车|下车|割肉|投资|囤");

    internal static bool IsGoldForecastAsk(string message) =>
        GoldFutureAsk.IsMatch(message) || GoldMaybeAsk.IsMatch(message) && !GoldNowWord.IsMatch(message);

    // Only the four horizons the model has; anything else (后天, 年底, 今年…) gets all four.
    internal static int? GoldHorizonAsked(string message) =>
        Regex.IsMatch(message, "明天|明日|隔天|下个交易日|一天") ? 1
        : Regex.IsMatch(message, "周(?!年)|星期|礼拜") ? 5
        : Regex.IsMatch(message, "月") ? 21
        : Regex.IsMatch(message, "明年|一年|年后|全年|[0-9一两二三]+年") ? CompanionGoldForecast.YearDays
        : null;

    // A question about another metal than the one being watched is not answered with the wrong numbers.
    private string? GoldOtherMetal(string message)
    {
        string watched = preferences.GoldVariety.StartsWith("AG", StringComparison.Ordinal) ? "AG"
            : preferences.GoldVariety.StartsWith("PT", StringComparison.Ordinal) ? "PT" : "AU";
        var asked = new List<(string Family, string Name)>();
        if (Regex.IsMatch(message, "黄金|(?<![铂白银])金价|金子|金条|[Aa][Uu]\\s?(99|\\(?[Tt]|100)")) asked.Add(("AU", "黄金"));
        if (Regex.IsMatch(message, "白银|银价|银条|[Aa][Gg]\\s?\\(?[Tt]")) asked.Add(("AG", "白银"));
        if (Regex.IsMatch(message, "铂金|铂价|[Pp][Tt]\\s?99")) asked.Add(("PT", "铂金"));
        if (asked.Count == 0 || asked.Any(a => a.Family == watched)) return null;
        string switchTo = asked[0].Family switch { "AG" => "Ag(T+D)", "PT" => "Pt99.95", _ => "Au99.99" };
        return $"鱼鱼现在盯的是 {GoldForecastName}，{asked[0].Name}要在 设置 → 互动与关怀 → 招商银行金价 里把品种换成 {switchTo}，换好再问我～";
    }

    private void AnswerGoldForecast(string message)
    {
        if (!preferences.GoldWatch)
        {
            ChatReply(message, "金价监测在设置里关掉了哦（设置 → 互动与关怀 → 招商银行金价），打开后鱼鱼才能帮你推算～", remember: false);
            return;
        }
        int? days = GoldHorizonAsked(message);
        bool advice = GoldAdviceAsk.IsMatch(message);
        if (goldQuote == null && !goldFetching) _ = FetchGold(false);
        UpdateGoldForecast();
        if (goldForecast != null)
        {
            ChatReply(message, GoldForecastText(days, advice), "余额-钱袋如常", remember: false);
            MaybeRefreshGoldHistory();
            return;
        }
        ChatReply(message, "鱼鱼去翻一下这些年的金价日线，算好马上告诉你～", "余额-钱袋如常", remember: false);
        _ = AnswerGoldForecastLater(days, advice);
    }

    private async Task AnswerGoldForecastLater(int? days, bool advice)
    {
        await RefreshGoldHistory(true);
        if (companionClosed) return;
        Say(goldForecast != null ? GoldForecastText(days, advice) : "呜……没拿到历史日线（" + goldHistoryStatus.TrimEnd('。') + "），等会儿再问我好不好？",
            "余额-钱袋如常");
    }

    // ---------------------------------------------------------------- settings: the forecast table
    private void AddGoldForecastSettings(StackPanel page)
    {
        page.Children.Add(new TextBlock { Text = "金价推测（天 / 周 / 月 / 年）", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        page.Children.Add(new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            Text = "用上海黄金交易所同一品种的历史日线（来自东方财富）和波动率模型，推算之后的价格区间：按历史波动，约 68% / 95% 的情况会落在其中。"
                + "中间值只算了长期平均涨幅的一半，不代表看涨或看跌。这是统计推算，不是预言，也不构成投资建议；周期越长越不准，一年的区间只能当个大概。" });
        var table = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        foreach (double width in new double[] { 96, 120, 132, 132, 84 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        page.Children.Add(table);
        var trend = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        page.Children.Add(trend);
        var score = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        page.Children.Add(score);
        var status = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        page.Children.Add(status);

        TextBlock Cell(string text, int row, int column, bool header = false, Brush? brush = null)
        {
            var cell = new TextBlock { Text = text, Margin = new Thickness(0, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                Foreground = brush ?? (header ? Brushes.SlateGray : Brushes.Black), FontSize = header ? 11 : 12 };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            table.Children.Add(cell);
            return cell;
        }
        void Fill()
        {
            UpdateGoldForecast();
            table.Children.Clear();
            table.RowDefinitions.Clear();
            var f = goldForecast;
            status.Text = goldHistoryStatus + (preferences.GoldVariety == "AU100G" ? " Au100g 没有单独的日线，按 Au99.99 的波动推算。" : "");
            if (f == null)
            {
                table.RowDefinitions.Add(new RowDefinition());
                Grid.SetColumnSpan(Cell(goldHistoryLoading ? "正在读取历史日线……"
                    : goldOffline ? "测试模式下不联网。" : "还没有足够的历史日线（至少 300 个交易日）。", 0, 0), 5);
                trend.Text = "";
                score.Text = "";
                return;
            }
            string P(double v) => CompanionGoldForecast.Price(v);
            for (int i = 0; i <= f.Rows.Count; i++) table.RowDefinitions.Add(new RowDefinition());
            Cell("周期", 0, 0, true); Cell("中间值", 0, 1, true); Cell("68% 区间", 0, 2, true); Cell("95% 区间", 0, 3, true); Cell("历史上涨占比", 0, 4, true);
            for (int i = 0; i < f.Rows.Count; i++)
            {
                var r = f.Rows[i];
                Cell(r.Label, i + 1, 0);
                Cell(P(r.Center), i + 1, 1);
                Cell($"{P(r.Low68)}～{P(r.High68)}", i + 1, 2);
                Cell($"{P(r.Low95)}～{P(r.High95)}", i + 1, 3, brush: Brushes.DimGray);
                Cell(double.IsFinite(r.UpShare) ? $"{r.UpShare * 100:0}%" : "—", i + 1, 4);
            }
            trend.Text = $"基准：{(f.LiveBase ? "招行现价" : $"{f.BaseDate.Month} 月 {f.BaseDate.Day} 日收盘")} {P(f.Base)} {CompanionGoldForecast.Unit(f.Code)}；"
                + $"日波动 {f.DailyVolatility * 100:0.00}%（近 3 年平均 {f.LongVolatility * 100:0.00}%）。{f.Trend}";
            score.Text = GoldForecastScoreText();
        }
        // Kept only while the page is on screen, so a closed settings window is not held or redrawn.
        Action view = Fill;
        goldForecastView = view;
        table.Loaded += (_, _) => { if (goldForecastView != view) { goldForecastView = view; view(); } };
        table.Unloaded += (_, _) => { if (goldForecastView == view) goldForecastView = null; };
        view();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        var refresh = BigBlueFish.Deployment.ModernUI.Button("更新推测", async () =>
        {
            if (goldHistoryTask is { IsCompleted: false }) return;
            await RefreshGoldHistory(true);
        });
        refresh.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(refresh);
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button("打开推测记录", () =>
        {
            try
            {
                var path = System.IO.Path.GetFullPath(GoldForecastFile);
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{System.IO.Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            }
            catch { }
        }));
        page.Children.Add(buttons);
        MaybeRefreshGoldHistory();
    }
}
