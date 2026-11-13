using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
    double High, double Low, string Time, double Average = 0)
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

    // Quotes with a real price. `snapshot` is the server's own clock ("2026-11-12 16:44"; it also runs while the market is
    // shut, so it says nothing about the market) and `signature` changes whenever anything in the feed moves: the exchange's
    // time, a price or a traded volume of any variety.
    internal static List<GoldQuote> Parse(string json, out string snapshot, out string signature)
    {
        snapshot = signature = "";
        var result = new List<GoldQuote>();
        var moving = new StringBuilder();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Text(root, "returnCode") != "SUC0000" || !root.TryGetProperty("body", out var body)) return result;
        snapshot = Text(body, "time");
        if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var quote = new GoldQuote(Text(item, "variety"), Text(item, "goldNo"), Number(item, "curPrice"), Number(item, "upDown"),
                Number(item, "open"), Number(item, "preClose"), Number(item, "high"), Number(item, "low"), Text(item, "time"), Number(item, "avePrice"));
            moving.Append(quote.Code).Append(' ').Append(quote.Time).Append(' ').Append(quote.Price.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(' ').Append(Number(item, "tradeCount").ToString("0", CultureInfo.InvariantCulture)).Append(';');
            if (quote.Code.Length > 0 && quote.Price > 0) result.Add(quote);
        }
        signature = moving.ToString();
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

    // When the session that is trading at `local` began (only meaningful while MarketOpen).
    internal static DateTime SessionStart(DateTime local)
    {
        var t = local.TimeOfDay;
        if (t < new TimeSpan(3, 0, 0)) return local.Date.AddDays(-1).AddHours(20);
        if (t < new TimeSpan(12, 0, 0)) return local.Date.AddHours(9);
        if (t < new TimeSpan(16, 0, 0)) return local.Date.AddHours(13.5);
        return local.Date.AddHours(20);
    }

    // The timetable says the exchange trades, but a look at the feed ten minutes or more after it last moved still found
    // nothing new: a public holiday (the timetable does not know them) or the feed has stopped. Every session gets ten
    // minutes to show that it is alive, and a slow update rate (每小时, 每天) is not mistaken for a market standing still.
    internal static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(10);

    internal static bool Stalled(DateTime local, DateTime lastMove, DateTime lastLook)
    {
        if (!MarketOpen(local)) return false;
        var start = SessionStart(local);
        return lastLook - (lastMove > start ? lastMove : start) >= StallAfter;
    }

    // The next moment worth looking at while the market is shut: a session opening (9:00, 13:30, 20:00 on weekdays) or the
    // final price just after a close (2:30, 15:30).
    internal static DateTime NextEvent(DateTime local)
    {
        for (int d = 0; d < 8; d++)
        {
            var day = local.Date.AddDays(d);
            bool weekday = day.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
            bool afterNight = day.DayOfWeek is >= DayOfWeek.Tuesday and <= DayOfWeek.Saturday;
            foreach (var (time, on) in new[] { (new TimeSpan(2, 31, 30), afterNight), (new TimeSpan(9, 0, 0), weekday), (new TimeSpan(13, 30, 0), weekday),
                         (new TimeSpan(15, 31, 30), weekday), (new TimeSpan(20, 0, 0), weekday) })
                if (on && day.Add(time) > local) return day.Add(time);
        }
        return local.AddDays(1);
    }

    // Seconds to wait before the next request: the chosen rate while the market really trades, at most every ten minutes
    // while it is shut or stands still, and after failures 5 s, 15 s, 1 min, 5 min, then every 10 min.
    internal static int NextDelay(int interval, bool open, bool stalled, int failures)
    {
        int normal = open && !stalled ? interval : Math.Max(interval, 600);
        int retry = failures switch { <= 0 => 0, 1 => 5, 2 => 15, 3 => 60, 4 => 300, _ => 600 };
        return Math.Max(normal, retry);
    }

    // "850 860，900" → the price levels to watch: positive, different, ascending, at most eight.
    internal static List<double> ParseLevels(string? text) =>
        (text ?? "").Split(new[] { ' ', ',', '，', '、', ';', '；', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0 && value < 1_000_000 ? Math.Round(value, 2) : 0)
            .Where(value => value > 0).Distinct().OrderBy(value => value).Take(8).ToList();

    internal static string FormatLevels(IEnumerable<double> levels) =>
        string.Join(" ", levels.Select(level => level.ToString("0.##", CultureInfo.InvariantCulture)));

    // One watched level and the newest price: +1 when the price has just come up to or through the level, −1 when it has just
    // fallen below it, 0 otherwise. After an alert the level stays quiet until the price has been 0.1 % away from it, so a price
    // that hovers on the level does not alert over and over; a move straight through to 0.1 % beyond it does count.
    internal static int Cross(ref int side, ref bool armed, double level, double price)
    {
        int now = price >= level ? 1 : -1;
        if (side == 0) { side = now; armed = true; return 0; } // the first price seen: nothing has been crossed yet
        bool away = Math.Abs(price - level) >= Math.Max(level * 0.001, 0.01);
        if (now != side)
        {
            side = now;
            if (armed || away) { armed = false; return now; }
            return 0;
        }
        if (!armed && away) armed = true;
        return 0;
    }
}

// Real-time CMB gold price: polls the quote, records every change in data\gold-records\ (one CSV a month), tells the owner
// about big moves, the price levels she was asked to watch and the day's percentage move, sums the day up after the close,
// and answers "金价多少" in chat.
public partial class MainWindow
{
    private readonly HttpClient goldWeb = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
    private GoldQuote? goldQuote;
    private string goldSnapshot = "", goldStatus = "还没有获取金价。";
    private DateTime nextGoldFetch = DateTime.MinValue, goldDay = DateTime.MinValue;
    private bool goldFetching, goldHighArmed = true, goldLowArmed = true;
    private int goldFailures;
    private double goldAnnounced = double.NaN, goldLastRecorded = double.NaN;
    private readonly List<(DateTime Local, double Price)> goldToday = new();
    private readonly List<string> goldChanges = new(); // newest last, for the settings page
    // Is the feed alive? Local times of the last successful request, of the last change anywhere in the feed, and of the
    // last change that was seen happening (the first look after starting does not count).
    private string goldFeedSignature = "";
    private DateTime goldFetchedAt = DateTime.MinValue, goldFeedMoved = DateTime.MinValue, goldTradingSeen = DateTime.MinValue;
    // The extra price levels (设置里的「其他到价提醒」) and the day's percentage steps that have been announced.
    private readonly Dictionary<double, (int Side, bool Armed, DateTime Alerted)> goldLevelState = new();
    private static readonly TimeSpan GoldLevelQuiet = TimeSpan.FromMinutes(15); // between two alerts for the same level
    private List<double> goldLevels = new();
    private string goldLevelsText = "\0", goldPercentKey = "";
    private int goldPercentUp, goldPercentDown;
    private DateTime nextGoldCloseLook = DateTime.MinValue;

    private bool GoldStalled => CompanionGoldFeed.Stalled(DateTime.Now, goldFeedMoved, goldFetchedAt);

    private string GoldName => CompanionGoldFeed.Varieties.FirstOrDefault(v => v.Code == preferences.GoldVariety).Name ?? preferences.GoldVariety;

    private void InitializeGold()
    {
        if (!goldWeb.DefaultRequestHeaders.UserAgent.Any())
            goldWeb.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BigBlueFish");
        // Earlier versions kept one gold-records.csv and dropped the oldest half at 5 MB; it becomes monthly files once.
        // If that fails (the file is open in Excel …) it stays where it is and is tried again at the next start.
        try { CompanionGoldRecords.Migrate(""); } catch { }
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
        GoldCloseSummaryTick();
        if (!preferences.GoldWatch || goldFetching || now < nextGoldFetch) return;
        nextGoldFetch = now.AddSeconds(GoldFetchDelay());
        _ = FetchGold();
    }

    // The chosen rate (每秒 … 每天) while the market really trades. While it is shut, or stands still on a public holiday, the
    // quote does not move: at most every 10 minutes, but on time for the next opening and for the final price after a close.
    private int GoldFetchDelay()
    {
        var local = DateTime.Now;
        bool trading = CompanionGoldFeed.MarketOpen(local) && !GoldStalled;
        int delay = CompanionGoldFeed.NextDelay(preferences.GoldIntervalSeconds, trading, false, goldFailures);
        if (!trading && goldFailures == 0 && preferences.GoldIntervalSeconds < 600)
            delay = (int)Math.Clamp((CompanionGoldFeed.NextEvent(local) - local).TotalSeconds + 1, 1, delay);
        return delay;
    }

    private static string GoldWait(int seconds) => seconds >= 60 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";

    private async Task FetchGold(bool announce = true)
    {
        if (goldOffline) return;
        goldFetching = true;
        try
        {
            string json = await goldWeb.GetStringAsync(CompanionGoldFeed.Url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var quotes = CompanionGoldFeed.Parse(json, out string snapshot, out string signature);
            if (quotes.Count == 0) throw new InvalidDataException("招行没有返回行情");
            var local = DateTime.Now;
            goldFailures = 0;
            goldFetchedAt = local;
            goldSnapshot = snapshot;
            goldAllQuotes = quotes;
            if (signature != goldFeedSignature)
            {
                if (goldFeedSignature.Length > 0) goldTradingSeen = local;
                goldFeedSignature = signature;
                goldFeedMoved = local;
            }
            var quote = quotes.FirstOrDefault(q => q.Code == preferences.GoldVariety);
            if (quote == null) { goldStatus = $"{local:HH:mm:ss} 行情里暂时没有 {GoldName} 的报价。"; return; }
            goldStatus = $"{local:HH:mm:ss} 已更新（行情时间 {quote.Time}）。";
            OnGoldQuote(quote, announce);
        }
        catch (Exception ex)
        {
            goldFailures++;
            int wait = GoldFetchDelay();
            nextGoldFetch = DateTime.UtcNow.AddSeconds(wait);
            goldStatus = $"{DateTime.Now:HH:mm:ss} 获取失败（连续 {goldFailures} 次）：{(ex is TaskCanceledException ? "连接超时" : ex.Message)}；{GoldWait(wait)}后再试。";
        }
        finally { goldFetching = false; }
    }

    // How a sentence about the price ends: "。", "（现在休市）。", or why the price may not be the latest.
    private string GoldMarketNote()
    {
        var local = DateTime.Now;
        if (goldFailures >= 2 && goldFetchedAt != DateTime.MinValue && local - goldFetchedAt > TimeSpan.FromMinutes(2))
            return $"（连不上招行，这是 {(goldFetchedAt.Date == local.Date ? goldFetchedAt.ToString("HH:mm") : goldFetchedAt.ToString("M 月 d 日 HH:mm"))} 的价格）。";
        if (!CompanionGoldFeed.MarketOpen(local)) return "（现在休市）。";
        if (GoldStalled) return $"（交易时段里行情没有更新{(goldQuote is { Time.Length: > 0 } q ? "，停在 " + q.Time : "")}，可能是节假日休市）。";
        return "。";
    }

    // The same for the status lines of the settings page and the gold panel.
    private string GoldFeedNote() =>
        GoldStalled ? " 交易时段里行情十分钟没有任何变化，按休市处理：每 10 分钟看一次，恢复后自动跟上。"
        : CompanionGoldFeed.MarketOpen(DateTime.Now) ? "" : " 现在休市，显示的是最近的价格。";

    private void OnGoldQuote(GoldQuote quote, bool announce)
    {
        var local = DateTime.Now;
        if (goldDay != local.Date) { goldDay = local.Date; goldToday.Clear(); }
        bool changed = goldQuote == null || goldQuote.Price != quote.Price || goldQuote.Code != quote.Code;
        double previous = goldQuote != null && goldQuote.Code == quote.Code ? goldQuote.Price : double.NaN;
        goldQuote = quote;
        // a new trading day (the previous close changes), another variety or another step: the day's percentage steps start
        // again; the ones already announced today survive a restart
        string dayKey = string.Join("|", quote.Code, quote.PreClose.ToString("0.00", CultureInfo.InvariantCulture),
            preferences.GoldPercentAlert.ToString("0.##", CultureInfo.InvariantCulture));
        if (dayKey != goldPercentKey)
        {
            goldPercentKey = dayKey;
            goldPercentUp = goldPercentDown = 0;
            var seen = preferences.GoldPercentSeen.Split('|');
            if (seen.Length == 5 && string.Join("|", seen[0], seen[1], seen[2]) == dayKey
                && int.TryParse(seen[3], NumberStyles.None, CultureInfo.InvariantCulture, out int seenUp)
                && int.TryParse(seen[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seenDown))
            {
                goldPercentUp = seenUp;
                goldPercentDown = seenDown;
            }
        }
        if (changed) NotifyGoldViews(); // the forecast's middle and the panel follow the live price
        if (!changed && !double.IsNaN(goldLastRecorded))
        {
            // a step of the day's percentage that she could not announce earlier because she was busy
            if (announce && TryGoldPercentAlert(quote)) goldAnnounced = quote.Price;
            return;
        }
        if (quote.Price != goldLastRecorded) RecordGold(quote, local);
        bool ruled = false; // an owner's rule for this change replaces the built-in change alert
        if (!double.IsNaN(previous) && quote.Price != previous)
        {
            double change = quote.Price - previous;
            ruled = PetEvent("gold.change", change > 0 ? "涨" : "跌", change, new Dictionary<string, string>
            { ["价格"] = quote.Price.ToString("0.00"), ["变动"] = change.ToString("+0.00;-0.00"), ["品种"] = quote.Variety, ["单位"] = quote.Unit });
        }
        if (!announce) return; // a look for the settings page or the panel: the alerts wait for the next regular look
        var (crossedUp, crossedDown) = GoldLevelsCrossed(quote.Price);

        // Watched levels: one alert each time the price crosses them; several at once share one bubble.
        var alerts = new List<(string Text, bool Up, string Kind)>();
        if (preferences.GoldAlertHigh > 0)
        {
            if (goldHighArmed && quote.Price >= preferences.GoldAlertHigh)
            {
                goldHighArmed = false;
                alerts.Add(($"{bond.Address}！招行{quote.Variety}涨到 {quote.Price:0.00} {quote.Unit} 啦，到你设的 {preferences.GoldAlertHigh:0.##} 了！", true, "高于"));
            }
            else if (quote.Price < preferences.GoldAlertHigh - 1) goldHighArmed = true;
        }
        if (preferences.GoldAlertLow > 0)
        {
            if (goldLowArmed && quote.Price <= preferences.GoldAlertLow)
            {
                goldLowArmed = false;
                alerts.Add(($"{bond.Address}！招行{quote.Variety}跌到 {quote.Price:0.00} {quote.Unit} 了，低于你设的 {preferences.GoldAlertLow:0.##}！", false, "低于"));
            }
            else if (quote.Price > preferences.GoldAlertLow + 1) goldLowArmed = true;
        }
        string Hello() => alerts.Count == 0 ? bond.Address + "！" : "";
        if (crossedUp.Count > 0)
            alerts.Add(($"{Hello()}招行{quote.Variety}涨过了你设的 {CompanionGoldFeed.FormatLevels(crossedUp).Replace(" ", "、")}，现在 {quote.Price:0.00} {quote.Unit}！", true, "涨到"));
        if (crossedDown.Count > 0)
            alerts.Add(($"{Hello()}招行{quote.Variety}跌破了你设的 {CompanionGoldFeed.FormatLevels(crossedDown).Replace(" ", "、")}，现在 {quote.Price:0.00} {quote.Unit}！", false, "跌到"));
        if (alerts.Count > 0)
        {
            string text = string.Join("\n", alerts.Select(a => a.Text));
            SayGold(text, alerts[0].Up, alerts[0].Up ? "余额-钱袋满溢" : "余额-数金皱眉", true);
            PetEvent("gold.alert", string.Join(" ", alerts.Select(a => a.Kind)), quote.Price,
                new Dictionary<string, string> { ["价格"] = quote.Price.ToString("0.00"), ["品种"] = quote.Variety }, said: text, important: true, ownReaction: true);
            goldAnnounced = quote.Price;
            return;
        }

        // 当日涨跌幅提醒.
        if (TryGoldPercentAlert(quote))
        {
            goldAnnounced = quote.Price;
            return;
        }

        // Big moves since she last mentioned the price.
        if (double.IsNaN(goldAnnounced)) goldAnnounced = quote.Price;
        double move = quote.Price - goldAnnounced;
        if (preferences.GoldAnnounce && Math.Abs(move) >= preferences.GoldStep && CanChatter && !GameActive)
        {
            goldAnnounced = quote.Price;
            string direction = move > 0 ? "涨" : "跌";
            SayGold($"招行金价{direction}了 {Math.Abs(move):0.##}！{quote.Variety} 现在 {quote.Price:0.00} {quote.Unit}，今天 {quote.UpDown:+0.00;-0.00}（{quote.Percent:+0.00;-0.00}%）",
                move > 0, move > 0 ? "余额-金袋叮当" : "余额-数金皱眉");
            return;
        }

        // 金价变动提醒: every change of the price, in red when it rose and in green when it fell.
        if (preferences.GoldChangeAlert && !ruled && !double.IsNaN(previous) && quote.Price != previous && GoldAlertAllowed)
        {
            double step = quote.Price - previous;
            SayGold($"招行 {quote.Variety} {quote.Price:0.00} {quote.Unit} {(step > 0 ? "↑" : "↓")}{Math.Abs(step):0.00}（{local:HH:mm:ss}，今天 {quote.UpDown:+0.00;-0.00}）",
                step > 0, null);
        }
    }

    // ---------------------------------------------------------------- extra levels, the day's percentage, the close
    private List<double> GoldLevels()
    {
        if (goldLevelsText != preferences.GoldAlertLevels)
        {
            goldLevelsText = preferences.GoldAlertLevels;
            goldLevels = CompanionGoldFeed.ParseLevels(goldLevelsText);
            foreach (double gone in goldLevelState.Keys.Where(level => !goldLevels.Contains(level)).ToList()) goldLevelState.Remove(gone);
        }
        return goldLevels;
    }

    // The extra levels the newest price has just crossed, upward and downward. A price swinging back and forth through a
    // level is reported at most once in fifteen minutes.
    private (List<double> Up, List<double> Down) GoldLevelsCrossed(double price)
    {
        var up = new List<double>();
        var down = new List<double>();
        var now = DateTime.UtcNow;
        foreach (double level in GoldLevels())
        {
            goldLevelState.TryGetValue(level, out var state);
            int side = state.Side;
            bool armed = state.Armed;
            int cross = CompanionGoldFeed.Cross(ref side, ref armed, level, price);
            if (cross != 0 && now - state.Alerted < GoldLevelQuiet) cross = 0;
            goldLevelState[level] = (side, armed, cross != 0 ? now : state.Alerted);
            if (cross > 0) up.Add(level);
            else if (cross < 0) down.Add(level);
        }
        return (up, down);
    }

    // 当日涨跌幅提醒: one line for every further multiple of the chosen percentage (1 %, 2 %, 3 % …), in each direction, per
    // trading day. A step reached while she cannot speak is announced as soon as she can, with the numbers of that moment.
    private bool TryGoldPercentAlert(GoldQuote quote)
    {
        double step = preferences.GoldPercentAlert;
        if (!(step > 0) || !(quote.PreClose > 0)) return false;
        int up = quote.Percent >= step ? (int)Math.Floor(quote.Percent / step + 1e-9) : 0;
        int down = quote.Percent <= -step ? (int)Math.Floor(-quote.Percent / step + 1e-9) : 0;
        bool rose = up > goldPercentUp, fell = down > goldPercentDown;
        if (!rose && !fell || !GoldAlertAllowed) return false;
        if (rose) goldPercentUp = up; else goldPercentDown = down;
        preferences.GoldPercentSeen = $"{goldPercentKey}|{goldPercentUp}|{goldPercentDown}";
        SaveCompanion();
        string line = $"招行{quote.Variety}今天已经{(rose ? "涨" : "跌")}了 {Math.Abs(quote.Percent):0.00}%（{quote.UpDown:+0.00;-0.00}），现在 {quote.Price:0.00} {quote.Unit}";
        SayGold(line, rose, rose ? "余额-金袋叮当" : "余额-数金皱眉");
        PetEvent("gold.alert", rose ? "涨幅" : "跌幅", quote.Percent, new Dictionary<string, string>
        { ["价格"] = quote.Price.ToString("0.00"), ["品种"] = quote.Variety, ["涨跌幅"] = quote.Percent.ToString("+0.00;-0.00") + "%" }, said: line, ownReaction: true);
        return true;
    }

    // The exchange traded today: the feed was seen moving in today's day session, or today's daily bar has been downloaded
    // (a public holiday has neither).
    private bool GoldTradedToday(DateTime local) =>
        goldTradingSeen.Date == local.Date && goldTradingSeen.TimeOfDay >= new TimeSpan(9, 0, 0)
        || goldBars.Count > 0 && goldBars[^1].Date.Date == local.Date && goldBarsCode == CompanionGoldForecast.HistoryCode(preferences.GoldVariety);

    // 收盘小结: once a trading day, after the day session has closed at 15:30, from a quote fetched after the close.
    // CareTick, every second — everything before the bubble is a cheap comparison.
    private void GoldCloseSummaryTick()
    {
        if (!preferences.GoldWatch || !preferences.GoldCloseSummary || goldQuote is not { } quote || quote.Code != preferences.GoldVariety) return;
        var local = DateTime.Now;
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        if (local.TimeOfDay < new TimeSpan(15, 32, 0) || local.TimeOfDay >= new TimeSpan(19, 55, 0)) return;
        string today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (preferences.GoldCloseSummaryDate == today) return;
        if (goldFetchedAt < local.Date.Add(new TimeSpan(15, 31, 0)))
        {
            // a slow update rate (每小时, 每天) would not look again in time: one look for the closing price
            if (!goldFetching && !goldOffline && DateTime.UtcNow >= nextGoldCloseLook)
            {
                nextGoldCloseLook = DateTime.UtcNow.AddMinutes(5);
                _ = FetchGold();
            }
            return;
        }
        // the day's final bar (downloaded from 15:35 on) says whether yesterday's range held; it gets until 15:45 to arrive
        bool barReady = goldBars.Count > 0 && goldBars[^1].Date.Date == local.Date && goldBarsFetched.ToLocalTime() >= local.Date.Add(new TimeSpan(15, 35, 0));
        if (!barReady && local.TimeOfDay < new TimeSpan(15, 45, 0)) return;
        if (!GoldTradedToday(local) || !GoldAlertAllowed) return;
        preferences.GoldCloseSummaryDate = today;
        SaveCompanion();
        string text = GoldCloseText(quote, local);
        bool up = quote.UpDown >= 0;
        SayGold(text, up, up ? "余额-金袋叮当" : "余额-数金皱眉");
        PetEvent("gold.close", up ? "涨" : "跌", quote.UpDown, new Dictionary<string, string>
        { ["价格"] = quote.Price.ToString("0.00"), ["变动"] = quote.UpDown.ToString("+0.00;-0.00"), ["品种"] = quote.Variety, ["单位"] = quote.Unit }, said: text, ownReaction: true);
    }

    private string GoldCloseText(GoldQuote quote, DateTime local)
    {
        var text = new StringBuilder($"今天收盘啦：招行 {quote.Variety} 收在 {quote.Price:0.00} {quote.Unit}，{(quote.UpDown >= 0 ? "涨" : "跌")}了 {Math.Abs(quote.UpDown):0.00}"
            + $"（{quote.Percent:+0.00;-0.00;0.00}%），最高 {quote.High:0.00}、最低 {quote.Low:0.00}。");
        if (GoldRangeForToday(local) is { } range)
        {
            string P(double v) => CompanionGoldForecast.Price(v);
            string verdict = quote.Price >= range.Low68 && quote.Price <= range.High68 ? "收在区间里"
                : quote.Price < range.Low95 || quote.Price > range.High95 ? "连 95% 的区间都没接住，今天波动很大"
                : quote.Price > range.High68 ? "比区间高，还在 95% 的区间里" : "比区间低，还在 95% 的区间里";
            text.Append($"\n上个交易日推测今天约 68% 的情况收在 {P(range.Low68)}～{P(range.High68)}：{verdict}。");
        }
        return text.Append("\n只是回顾，不构成投资建议。").ToString();
    }

    // The range forecast for today's close on the trading day before (gold-forecasts.csv), if she was running then.
    private GoldForecastRecord? GoldRangeForToday(DateTime local)
    {
        string code = preferences.GoldVariety;
        if (CompanionGoldForecast.HistoryCode(code) != code) return null;
        int index = goldBars.FindLastIndex(bar => bar.Date.Date == local.Date);
        if (index < 1) return null;
        var baseDate = goldBars[index - 1].Date.Date;
        return GoldForecastRecords().LastOrDefault(r => r.Code == code && r.Days == 1 && r.BaseDate.Date == baseDate);
    }

    // ---------------------------------------------------------------- red / green gold bubbles
    private Brush? goldBubbleDefault;
    private string? goldTintText;
    private bool goldTintImportant;

    // Not while she is busy with the owner or hidden, and never over another line that is still in her bubble
    // (a reply, a reminder, a price alert you asked for); an earlier change alert may be replaced by the newer price.
    private bool GoldAlertAllowed => companionReady && IsVisible && !hiddenByUser && !fullscreenHidden && !PuppetActive && !dragging
        && !exitAnimation && !FocusActive && !GameActive && chatWindow == null && loadingIndicator?.Visibility != Visibility.Visible
        && (bubble.Visibility != Visibility.Visible || goldTintText != null && speech.FullText == goldTintText && !goldTintImportant);

    // A gold line in her bubble: red text when the price rose, green when it fell. The colour goes back to normal as soon
    // as the bubble shows anything else.
    private void SayGold(string text, bool up, string? animation, bool important = false)
    {
        if (goldBubbleDefault == null)
        {
            goldBubbleDefault = bubbleText.Foreground;
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(bubbleText, (_, _) =>
            {
                if (goldTintText != null && speech.FullText != goldTintText) { goldTintText = null; bubbleText.Foreground = goldBubbleDefault; }
            });
        }
        Say(text, animation, important);
        if (speech.FullText != text) return; // not shown (hidden, puppet …)
        goldTintText = text;
        goldTintImportant = important;
        bubbleText.Foreground = up ? GoldUp : GoldDown;
    }

    private void RecordGold(GoldQuote quote, DateTime local)
    {
        double previous = goldLastRecorded;
        goldLastRecorded = quote.Price;
        goldToday.Add((local, quote.Price));
        if (goldToday.Count > 60000) goldToday.RemoveRange(0, goldToday.Count - 60000); // more than a day of changes every second
        string delta = double.IsNaN(previous) ? "" : $"（{quote.Price - previous:+0.00;-0.00}）";
        goldChanges.Add($"{local:HH:mm:ss}  {quote.Price:0.00}{delta}");
        if (goldChanges.Count > 50) goldChanges.RemoveRange(0, goldChanges.Count - 50);
        try
        {
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            CompanionGoldRecords.Append("", local, string.Join(",", local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                goldSnapshot.Length > 0 ? goldSnapshot : quote.Time, quote.Variety, F(quote.Price), F(quote.UpDown),
                quote.Percent.ToString("0.00", CultureInfo.InvariantCulture), F(quote.Open), F(quote.PreClose), F(quote.High), F(quote.Low)));
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
            string variety = CompanionGoldFeed.Varieties.FirstOrDefault(v => v.Code == preferences.GoldVariety).Name?.Split('（')[0] ?? "";
            double previous = double.NaN;
            foreach (var (time, price) in CompanionGoldRecords.Read("", variety, goldDay, 8L << 20))
            {
                if (time.Date != goldDay) continue;
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
        text.Append(GoldMarketNote());
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
        MaybeRefreshGoldIndicative();
        ChatReply(message, GoldTodayText(), goldQuote is { UpDown: >= 0 } ? "余额-金袋叮当" : "余额-数金皱眉", remember: false);
        return true;
    }

    // ---------------------------------------------------------------- settings
    private void AddGoldSettings(StackPanel page, Action<string> section, Action<string> note, Func<string, bool, Action<bool>, CheckBox> toggle)
    {
        section("招商银行金价");
        toggle("实时监测招商银行黄金行情（数据来自招行一网通「黄金市场行情」，价格每次变化都记到本机 data\\gold-records 文件夹，每月一个 CSV）",
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
            goldLevelState.Clear(); // the levels belong to the price of one variety
            goldIndicative = null;
            goldWeekKey = "";
            LoadGoldToday();
            nextGoldFetch = DateTime.UtcNow;
            nextGoldHistoryTry = DateTime.MinValue;
            NotifyGoldViews();
            if (preferences.GoldWatch) _ = RefreshGoldHistory(false);
        }));
        first.Children.Add(Caption("更新", 40));
        first.Children.Add(Choice(new List<(int, string)> { (1, "每秒"), (60, "每分钟"), (600, "每十分钟"), (3600, "每小时"), (86400, "每天") },
            preferences.GoldIntervalSeconds, s => { preferences.GoldIntervalSeconds = s; nextGoldFetch = DateTime.UtcNow; }));
        var second = Row();
        second.Children.Add(new CheckBox { Content = "涨跌超过", IsChecked = preferences.GoldAnnounce, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        ((CheckBox)second.Children[0]).Click += (_, e) => { preferences.GoldAnnounce = ((CheckBox)e.Source).IsChecked == true; SaveCompanion(); };
        second.Children.Add(Choice(new List<(double, string)> { (1, "1 元"), (2, "2 元"), (3, "3 元"), (5, "5 元"), (8, "8 元"), (10, "10 元"), (20, "20 元") },
            preferences.GoldStep, s => preferences.GoldStep = s));
        second.Children.Add(Caption("时鱼鱼播报（玩游戏、专注时不打扰）", 260));
        toggle("金价变动提醒：每次价格变化，鱼鱼都用气泡报当时的金价——涨了是红字，跌了是绿字（玩游戏、专注、聊天时不打扰）",
            preferences.GoldChangeAlert, v => preferences.GoldChangeAlert = v);
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
        var fourth = Row();
        fourth.Children.Add(Caption("其他到价提醒", 100));
        var levels = new TextBox { Width = 230, Text = preferences.GoldAlertLevels, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2, 4, 2) };
        string levelsShown = levels.Text;
        levels.LostFocus += (_, _) =>
        {
            if (levels.Text.Trim() == levelsShown) return;
            levels.Text = levelsShown = CompanionGoldFeed.FormatLevels(CompanionGoldFeed.ParseLevels(levels.Text));
            if (levelsShown == preferences.GoldAlertLevels) return;
            preferences.GoldAlertLevels = levelsShown;
            SaveCompanion();
            goldPanelView?.Invoke();
        };
        fourth.Children.Add(levels);
        fourth.Children.Add(Caption("多个价位用空格隔开（最多 8 个），涨过或跌破都提醒", 330));
        page.Children.Add(new TextBlock { Text = "留空 = 不提醒。金价面板里也能改，并能看到离现价多远、按波动粗算的触及可能性。", FontSize = 11,
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        var fifth = Row();
        fifth.Children.Add(Caption("当日涨跌幅每到", 100));
        fifth.Children.Add(Choice(CompanionPreferences.GoldPercentSteps.Select(p => (p, p > 0 ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "不提醒")).ToList(),
            preferences.GoldPercentAlert, p => preferences.GoldPercentAlert = p));
        fifth.Children.Add(Caption("的整数倍时提醒一次（选 1%：涨跌到 1%、2%、3%……各说一次）", 380));
        toggle("收盘小结：每个交易日 15:30 收盘后，说一句今天的涨跌、最高最低，以及上个交易日推测的区间准不准",
            preferences.GoldCloseSummary, v => preferences.GoldCloseSummary = v);

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
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button("打开记录文件夹", OpenGoldRecords));
        AddGoldForecastSettings(page);
        var (recordFiles, recordBytes) = CompanionGoldRecords.Size("");
        note(goldStatus + GoldFeedNote() + " 交易时段（9:00–11:30、13:30–15:30、20:00–次日 2:30）按选的频率更新；休市或节假日价格不动，最多每 10 分钟看一次，"
            + "开盘和收盘时会准点再看（选每小时、每天时照选的来）；连不上时隔 5 秒、15 秒、1 分钟、5 分钟、10 分钟再试。"
            + (recordFiles > 0 ? $"价格记录按月保存，不会自动删除：现在 {recordFiles} 个月、共 {recordBytes / 1048576.0:0.0} MB。" : "")
            + "右键鱼鱼「招行金价」，她用气泡告诉你现价和今天的预计区间；「金价面板…」用表格看金价变化、预测、买卖参考和判断依据；聊天时问「金价多少」「明天金价会涨吗」「黄金下个月走势」也行。");
    }

    // This month's record selected in Explorer (or the folder, or the data folder while nothing has been recorded yet).
    private void OpenGoldRecords()
    {
        try
        {
            string file = System.IO.Path.GetFullPath(CompanionGoldRecords.MonthFile("", DateTime.Now));
            string folder = System.IO.Path.GetDirectoryName(file)!;
            string argument = File.Exists(file) ? $"/select,\"{file}\"" : $"\"{(Directory.Exists(folder) ? folder : Environment.CurrentDirectory)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch { }
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
        var stroke = goldToday[^1].Price >= goldToday[0].Price ? new SolidColorBrush(Color.FromRgb(214, 64, 64)) : new SolidColorBrush(Color.FromRgb(40, 150, 90));
        // A day of changes every second is thinned for drawing, and the line is not drawn across a pause (lunch break, she was off).
        var points = CompanionGoldRecords.Thin(goldToday, 600);
        Polyline? line = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (line == null || points[i].Time - points[i - 1].Time > TimeSpan.FromMinutes(20))
                canvas.Children.Add(line = new Polyline { StrokeThickness = 1.6, Stroke = stroke });
            line.Points.Add(new Point(6 + (points[i].Time - start).TotalSeconds / span * (width - 12), 8 + (max - points[i].Price) / (max - min) * (height - 16)));
        }
        var labels = new TextBlock { Text = $"{max:0.00}", FontSize = 10, Foreground = Brushes.SlateGray };
        Canvas.SetLeft(labels, 4); Canvas.SetTop(labels, 0);
        canvas.Children.Add(labels);
        var low = new TextBlock { Text = $"{min:0.00}　{start:HH:mm}–{end:HH:mm}", FontSize = 10, Foreground = Brushes.SlateGray };
        Canvas.SetLeft(low, 4); Canvas.SetTop(low, height - 14);
        canvas.Children.Add(low);
        return frame;
    }
}
