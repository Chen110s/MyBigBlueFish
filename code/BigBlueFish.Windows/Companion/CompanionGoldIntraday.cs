using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

// What the last domestic price would be now if it had moved with international gold and the yuan since the domestic market
// last traded. Checked on the eight session breaks of 6–12 November 2026: half an hour to an hour after the market reopened the
// domestic price was 0.1–0.2 % away from this on average, against 0.5 % from the last close — a small sample, and the domestic
// premium does move (0.5 % on one of the eight), so it is shown as a reference and never as the price.
internal sealed record GoldIndicative(string Code, double Price, double Last, double InternationalPercent, double FxPercent,
    DateTime Anchor, DateTime AsOf)
{
    internal double Percent => Last > 0 ? (Price / Last - 1) * 100 : 0;
}

// EastMoney minute K-lines (the same source as the daily bars): 1-minute bars of the current trading day and 5-minute bars of
// about the last nine trading days for the Shanghai Gold Exchange contracts, 5-minute bars of about the last five days for
// international gold / silver and the offshore yuan. Times are Beijing time and mark the end of a bar.
internal static class CompanionGoldIntraday
{
    internal static string Url(string secid, int minutes, int limit) =>
        $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55&klt={minutes}&fqt=0&end=20500101&lmt={limit}";

    internal static string Secid(string historyCode) => "118." + historyCode;

    // "2026-11-12 15:30,851.34,851.33,851.40,850.50" → the end of the bar and its close; oldest first.
    internal static List<(DateTime Time, double Price)> Parse(string json)
    {
        var points = new List<(DateTime Time, double Price)>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("klines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return points;
        foreach (var line in lines.EnumerateArray())
        {
            var parts = (line.ValueKind == JsonValueKind.String ? line.GetString() ?? "" : "").Split(',');
            if (parts.Length < 3 || !DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double close) || !double.IsFinite(close) || close <= 0)
                continue;
            if (points.Count > 0 && time <= points[^1].Time) continue;
            points.Add((time, close));
        }
        return points;
    }

    // Indexes at which a new trading day starts (never index 0).
    internal static List<int> DayStarts(IReadOnlyList<(DateTime Time, double Price)> points)
    {
        var starts = new List<int>();
        for (int i = 1; i < points.Count; i++)
            if (CompanionGoldForecast.TradingDay(points[i].Time.AddMinutes(-1)) != CompanionGoldForecast.TradingDay(points[i - 1].Time.AddMinutes(-1)))
                starts.Add(i);
        return starts;
    }

    // Indexes at which trading resumes after a pause of more than half an hour (never index 0).
    internal static List<int> SessionStarts(IReadOnlyList<(DateTime Time, double Price)> points)
    {
        var starts = new List<int>();
        for (int i = 1; i < points.Count; i++)
            if (points[i].Time - points[i - 1].Time > TimeSpan.FromMinutes(30)) starts.Add(i);
        return starts;
    }

    // The last close at or before a moment, if it is no older than the tolerance.
    internal static double AsOf(IReadOnlyList<(DateTime Time, double Price)> points, DateTime time, TimeSpan tolerance)
    {
        double value = double.NaN;
        var at = DateTime.MinValue;
        foreach (var (t, price) in points)
        {
            if (t > time) break;
            value = price;
            at = t;
        }
        return time - at <= tolerance ? value : double.NaN;
    }

    // last × (international price × yuan now) ÷ (the same when the domestic market last traded); the domestic premium is taken
    // as unchanged. Null when the minute bars do not reach back to that moment or nothing has moved since.
    internal static GoldIndicative? Indicative(string code, double last, DateTime anchor,
        IReadOnlyList<(DateTime Time, double Price)> international, IReadOnlyList<(DateTime Time, double Price)> yuan)
    {
        if (!(last > 0) || international.Count == 0 || yuan.Count == 0) return null;
        var tolerance = TimeSpan.FromMinutes(45);
        double gold0 = AsOf(international, anchor, tolerance), yuan0 = AsOf(yuan, anchor, tolerance);
        if (!(gold0 > 0) || !(yuan0 > 0)) return null;
        var (goldTime, gold1) = international[^1];
        var (yuanTime, yuan1) = yuan[^1];
        if (goldTime <= anchor && yuanTime <= anchor) return null;
        double ratio = gold1 * yuan1 / (gold0 * yuan0);
        if (!double.IsFinite(ratio) || ratio < 0.9 || ratio > 1.1) return null; // a damaged bar, not a market move
        return new GoldIndicative(code, last * ratio, last, (gold1 / gold0 - 1) * 100, (yuan1 / yuan0 - 1) * 100, anchor,
            goldTime > yuanTime ? goldTime : yuanTime);
    }
}

// Minute bars for the 「今天」 and 「近 7 天」 charts of the gold panel (downloaded only while one of them is on screen) and the
// indicative price while the domestic market is shut (downloaded only when somebody asks for the price or looks at the panel).
public partial class MainWindow
{
    private readonly Dictionary<int, List<(DateTime Time, double Price)>> goldIntraday = new(); // minutes per bar → bars
    private readonly Dictionary<int, DateTime> goldIntradayFetched = new();
    private string goldIntradayCode = "", goldIntradayStatus = "";
    private DateTime nextGoldIntradayTry = DateTime.MinValue;
    private Task? goldIntradayTask;
    private GoldIndicative? goldIndicative;
    private DateTime goldIndicativeFetched = DateTime.MinValue, nextGoldIndicativeTry = DateTime.MinValue;
    private Task? goldIndicativeTask;

    // Shut by the timetable, or open by the timetable while nothing moves (a public holiday).
    private bool GoldClosedNow => !CompanionGoldFeed.MarketOpen(DateTime.Now) || GoldStalled;

    private List<(DateTime Time, double Price)>? GoldIntradayBars(int minutes) =>
        goldIntradayCode == CompanionGoldForecast.HistoryCode(preferences.GoldVariety) && goldIntraday.TryGetValue(minutes, out var bars) && bars.Count > 1 ? bars : null;

    // Every minute (1-minute bars) or five minutes (5-minute bars) while the market trades, every half hour otherwise.
    private void MaybeRefreshGoldIntraday(int minutes)
    {
        if (goldOffline || DateTime.UtcNow < nextGoldIntradayTry || goldIntradayTask is { IsCompleted: false }) return;
        string code = CompanionGoldForecast.HistoryCode(preferences.GoldVariety);
        var age = GoldClosedNow ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(minutes == 1 ? 1 : 5);
        if (goldIntradayCode == code && goldIntradayFetched.TryGetValue(minutes, out var fetched) && DateTime.UtcNow - fetched < age) return;
        goldIntradayTask = RefreshGoldIntraday(code, minutes);
    }

    private async Task RefreshGoldIntraday(string code, int minutes)
    {
        try
        {
            string json = await GoldHistoryWeb().GetStringAsync(CompanionGoldIntraday.Url(CompanionGoldIntraday.Secid(code), minutes, minutes == 1 ? 1000 : 1800));
            var bars = CompanionGoldIntraday.Parse(json);
            if (bars.Count == 0) throw new InvalidDataException("没有返回分钟线");
            if (goldIntradayCode != code)
            {
                goldIntraday.Clear();
                goldIntradayFetched.Clear();
                goldIntradayCode = code;
            }
            goldIntraday[minutes] = bars;
            goldIntradayFetched[minutes] = DateTime.UtcNow;
            goldIntradayStatus = "";
        }
        catch (Exception ex)
        {
            nextGoldIntradayTry = DateTime.UtcNow.AddMinutes(2);
            goldIntradayStatus = "分钟线暂时拿不到（" + (ex is TaskCanceledException ? "连接超时" : ex.Message) + "），先用鱼鱼自己记下的报价画图。";
        }
        if (!companionClosed) goldPanelView?.Invoke();
    }

    // The indicative price for the quote on screen, or null: the market trades, there is none, or it is out of date.
    private GoldIndicative? GoldIndicativeNow() =>
        goldIndicative is { } value && goldQuote is { } quote && value.Code == quote.Code && value.Last == quote.Price && GoldClosedNow
        && DateTime.UtcNow - goldIndicativeFetched < TimeSpan.FromMinutes(30) ? value : null;

    private void MaybeRefreshGoldIndicative()
    {
        if (goldOffline || !GoldClosedNow || goldQuote is not { } quote || CompanionGoldFactors.International(quote.Code) == null) return;
        if (goldIndicativeTask is { IsCompleted: false } || DateTime.UtcNow < nextGoldIndicativeTry) return;
        if (GoldIndicativeNow() != null && DateTime.UtcNow - goldIndicativeFetched < TimeSpan.FromMinutes(5)) return;
        goldIndicativeTask = RefreshGoldIndicative(quote);
    }

    private async Task RefreshGoldIndicative(GoldQuote quote)
    {
        try
        {
            var web = GoldHistoryWeb();
            string history = CompanionGoldForecast.HistoryCode(quote.Code);
            // when the domestic market last traded: the end of its newest 5-minute bar
            var domestic = CompanionGoldIntraday.Parse(await web.GetStringAsync(CompanionGoldIntraday.Url(CompanionGoldIntraday.Secid(history), 5, 3)));
            if (domestic.Count == 0) throw new InvalidDataException("没有返回国内分钟线");
            var (anchor, close) = domestic[^1];
            if (history == quote.Code && Math.Abs(close / quote.Price - 1) > 0.005) throw new InvalidDataException("分钟线和招行报价对不上");
            int bars = (int)Math.Clamp((DateTime.Now - anchor).TotalMinutes / 5 + 30, 40, 1800);
            string international = CompanionGoldFactors.Series.First(s => s.Key == CompanionGoldFactors.International(quote.Code)).Secid;
            string yuan = CompanionGoldFactors.Series.First(s => s.Key == "CNH").Secid;
            var goldTask = web.GetStringAsync(CompanionGoldIntraday.Url(international, 5, bars));
            var yuanTask = web.GetStringAsync(CompanionGoldIntraday.Url(yuan, 5, bars));
            await Task.WhenAll(goldTask, yuanTask);
            goldIndicative = CompanionGoldIntraday.Indicative(quote.Code, quote.Price, anchor,
                CompanionGoldIntraday.Parse(goldTask.Result), CompanionGoldIntraday.Parse(yuanTask.Result));
            goldIndicativeFetched = DateTime.UtcNow;
            // e.g. a holiday longer than the minute bars reach back: no use asking again every few minutes
            if (goldIndicative == null) nextGoldIndicativeTry = DateTime.UtcNow.AddMinutes(15);
        }
        catch
        {
            nextGoldIndicativeTry = DateTime.UtcNow.AddMinutes(5);
        }
        if (!companionClosed) NotifyGoldViews();
    }

    // "按国际金价（+0.71%）和人民币汇率（-0.05%）折算，现在大约 857.0 元/克（比最后成交 +0.66%）"
    private string GoldIndicativeText(GoldIndicative value)
    {
        static string Signed(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
        string metal = CompanionGoldFactors.International(value.Code) == "XAG" ? "国际银价" : "国际金价";
        string asOf = DateTime.Now - value.AsOf > TimeSpan.FromMinutes(45) ? $"（国际行情截至 {value.AsOf:M 月 d 日 HH:mm}）" : "";
        return $"按{metal}（{Signed(value.InternationalPercent)}）和人民币汇率（{Signed(value.FxPercent)}）折算，现在大约 "
            + $"{CompanionGoldForecast.Price(value.Price)} {CompanionGoldForecast.Unit(value.Code)}（比最后成交 {Signed(value.Percent)}）{asOf}";
    }
}
