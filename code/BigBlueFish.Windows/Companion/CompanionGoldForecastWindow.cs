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
        NotifyGoldViews();
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

    // Right-click 「招行金价」 and "金价多少": the price now and the expected range of the next close (today's while trading).
    private string GoldTodayText(bool panel = false)
    {
        if (goldQuote is not { } q) return GoldSummary();
        string P(double v) => CompanionGoldForecast.Price(v);
        var text = new StringBuilder($"招行 {q.Variety} 现在 {q.Price:0.00} {q.Unit}，今天 {q.UpDown:+0.00;-0.00;0.00}（{q.Percent:+0.00;-0.00;0.00}%），"
            + $"开盘 {q.Open:0.00}、最高 {q.High:0.00}、最低 {q.Low:0.00}");
        text.Append(CompanionGoldFeed.MarketOpen(DateTime.Now) ? "。" : "（现在休市）。");
        UpdateGoldForecast();
        if (goldForecast is { } f && q.Code == preferences.GoldVariety)
        {
            var s = CompanionGoldForecast.Session(f, q.Price, DateTime.Now);
            text.Append($"\n{s.Label}预计：按近期波动，约 68% 的情况在 {P(s.Low68)}～{P(s.High68)}，约 95% 在 {P(s.Low95)}～{P(s.High95)}。");
        }
        else text.Append("\n今天的预计还在准备（要先读历史日线），过一会儿再问我～");
        string moves = CompanionGoldFactors.DayMoves(preferences.GoldVariety, goldFactorBars);
        if (moves.Length > 0) text.Append('\n').Append(moves);
        text.Append(panel ? "\n只是统计推算，不构成投资建议。" : "\n只是统计推算，不构成投资建议；表格在右键「金价面板」里。");
        return text.ToString();
    }

    private async Task SayGoldNow()
    {
        MaybeRefreshGoldFactors();
        MaybeRefreshGoldHistory();
        if (!goldOffline && (goldQuote == null || goldBars.Count < CompanionGoldForecast.MinimumDays))
        {
            Say("鱼鱼去招行看看金价～", "余额-钱袋如常");
            if (goldQuote == null)
            {
                if (!goldFetching) await FetchGold(preferences.GoldWatch);
                for (int i = 0; i < 100 && goldFetching; i++) await Task.Delay(100);
            }
            if (goldBars.Count < CompanionGoldForecast.MinimumDays && DateTime.UtcNow >= nextGoldHistoryTry) await RefreshGoldHistory(false);
        }
        if (companionClosed) return;
        Say(GoldTodayText(), goldQuote is { UpDown: >= 0 } ? "余额-金袋叮当" : goldQuote != null ? "余额-数金皱眉" : null);
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
        if (days == 0)
        {
            var s = CompanionGoldForecast.Session(f, f.Base, DateTime.Now);
            text.Append($"招行 {GoldForecastName} {basis}。按近期波动推算，{s.Label}约 68% 的情况在 {P(s.Low68)}～{P(s.High68)}，约 95% 在 {P(s.Low95)}～{P(s.High95)}。");
        }
        else if (days is int d && f.Row(d) is { } row)
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
            var session = CompanionGoldForecast.Session(f, f.Base, DateTime.Now);
            text.Append($"\n{session.Label}：{P(session.Low68)}～{P(session.High68)}");
            foreach (var r in f.Rows) text.Append($"\n{r.Label}：{P(r.Low68)}～{P(r.High68)}");
        }
        text.Append('\n').Append(f.Trend);
        string brief = CompanionGoldFactors.Brief(f.Code, goldFactorBars);
        if (brief.Length > 0) text.Append('\n').Append(brief);
        text.Append("\n只是统计推算，不是预言，也不构成投资建议～表格、判断因素和点差回本在右键「招行金价」的金价面板里。");
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

    // Today's session (0) or the four horizons the model has; anything else (后天, 年底, 今年…) gets all four.
    internal static int? GoldHorizonAsked(string message) =>
        Regex.IsMatch(message, "明天|明日|隔天|下个交易日|一天") ? 1
        : Regex.IsMatch(message, "今天|今日|收盘") ? 0
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
            MaybeRefreshGoldFactors();
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

    // ---------------------------------------------------------------- settings: short summary, the tables live in the gold panel
    private void AddGoldForecastSettings(StackPanel page)
    {
        page.Children.Add(new TextBlock { Text = "金价面板与推测（天 / 周 / 月 / 年）", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        page.Children.Add(new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            Text = "金价面板用表格列出：实时行情、价格走势图、天 / 周 / 月 / 年推测、判断因素（国际金价、美联储预期、美元、避险、央行购金、人民币汇率、产品点差）、"
                + "国内金价拆解、点差回本、你的计划价位和历史参考价位。推测用上海黄金交易所同一品种的历史日线和波动率模型，是统计推算，不构成投资建议。" });
        var score = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        page.Children.Add(score);
        void Fill()
        {
            UpdateGoldForecast();
            var f = goldForecast;
            score.Text = (f != null && f.Row(1) is { } day && f.Row(21) is { } month
                    ? $"按历史波动，约 68% 的情况：下个交易日 {CompanionGoldForecast.Price(day.Low68)}～{CompanionGoldForecast.Price(day.High68)}，"
                      + $"一个月 {CompanionGoldForecast.Price(month.Low68)}～{CompanionGoldForecast.Price(month.High68)}。"
                    : "")
                + GoldForecastScoreText() + " " + goldHistoryStatus
                + (preferences.GoldVariety == "AU100G" ? " Au100g 没有单独的日线，按 Au99.99 的波动推算。" : "");
        }
        // Kept only while the page is on screen, so a closed settings window is not held or redrawn.
        Action view = Fill;
        goldForecastView = view;
        score.Loaded += (_, _) => { if (goldForecastView != view) { goldForecastView = view; view(); } };
        score.Unloaded += (_, _) => { if (goldForecastView == view) goldForecastView = null; };
        view();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        var panel = BigBlueFish.Deployment.ModernUI.Button("打开金价面板", ShowGoldPanel, true);
        panel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(panel);
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
