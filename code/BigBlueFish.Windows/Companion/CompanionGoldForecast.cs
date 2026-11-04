using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Windows;

internal sealed record GoldBar(DateTime Date, double Open, double Close, double High, double Low);

// One horizon of a forecast: the middle estimate and the ranges that should hold the price about 68 % / 95 % of the time.
internal sealed record GoldForecastRow(string Label, int Days, double Center, double Low68, double High68, double Low95, double High95,
    double UpShare, double Sigma);

internal sealed record GoldForecast(string Code, double Base, bool LiveBase, DateTime BaseDate, IReadOnlyList<GoldForecastRow> Rows,
    double DailyVolatility, double LongVolatility, double AnnualDrift, string Trend, int HistoryDays, DateTime FirstDay)
{
    internal GoldForecastRow? Row(int days) => Rows.FirstOrDefault(r => r.Days == days);
    internal double Years => Math.Max(0, (BaseDate - FirstDay).TotalDays / 365.25);
}

// A forecast written to gold-forecasts.csv, checked against the real close once its day has come.
internal sealed record GoldForecastRecord(DateTime Made, string Code, DateTime BaseDate, int Days, double Base, double Center,
    double Low68, double High68, double Low95, double High95);

internal sealed record GoldForecastScore(int Days, int Recorded, int Checked, int Inside68, int Inside95, double MeanMissPercent);

// 金价推测: a statistical range forecast, not a prediction of direction.
//  - volatility now: exponentially weighted daily returns (λ = 0.97); it drifts back to the ~3-year level over the horizon
//    (persistence 0.985 per day), the way calm or stormy spells fade;
//  - trend: half of the long-run average daily return (gold's long drift, shrunk because the future one is unknown), plus
//    ±10 %/year of uncertainty about that trend, which mostly widens the one-year range;
//  - ranges: ±1.0 σ (≈68 %) and ±2.2 σ (≈95 %; wider than 1.96 because gold has fat tails).
// Back-tested on SGE Au99.99 daily closes 2013–2026 (re-estimated every day): the 1-day / 1-week / 1-month ranges held
// 72–75 % / about 95 % of the time (about 68 % / 90 % for a month in the stormy last three years); the 1-year range held
// 66 % / 95 % overall but only 17 % / 73 % through the 2024–2025 bull run, which is why the one-year line is only a rough idea.
internal static class CompanionGoldForecast
{
    internal const int YearDays = 244; // SGE trading days per year
    internal static readonly (string Label, int Days)[] Horizons = [("下个交易日", 1), ("一周", 5), ("一个月", 21), ("一年", YearDays)];
    internal const double Z68 = 1.0, Z95 = 2.2;
    private const double Lambda = 0.97, Persistence = 0.985, DriftShrink = 0.5, DriftUncertainty = 0.10;
    private const int LongWindow = 750, SeedDays = 20;
    internal const int MinimumDays = 300;

    // Au100g has no daily history of its own; it moves with Au99.99.
    internal static string HistoryCode(string code) => code == "AU100G" ? "AU9999" : code;

    // EastMoney daily K-lines of the Shanghai Gold Exchange (market 118); fields: date, open, close, high, low.
    internal static string HistoryUrl(string code, DateTime? since) =>
        "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=118." + HistoryCode(code)
        + "&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55&klt=101&fqt=0&beg="
        + (since ?? new DateTime(2010, 1, 1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "&end=20500101&lmt=10000";

    internal static List<GoldBar> ParseHistory(string json)
    {
        var bars = new List<GoldBar>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("klines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return bars;
        foreach (var line in lines.EnumerateArray())
            if (line.ValueKind == JsonValueKind.String && ParseBar(line.GetString()) is { } bar) bars.Add(bar);
        return Clean(bars);
    }

    // "2026-11-02,851.00,836.21,851.00,830.10"
    internal static GoldBar? ParseBar(string? text)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length < 5 || !DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var values = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return null;
        return values[1] > 0 ? new GoldBar(date, values[0], values[1], values[2], values[3]) : null;
    }

    // Oldest first, one bar per day; for a day seen twice the later bar wins (fresh data over the cache).
    internal static List<GoldBar> Clean(IEnumerable<GoldBar> bars) =>
        bars.GroupBy(b => b.Date.Date).Select(g => g.Last() with { Date = g.Key }).OrderBy(b => b.Date).ToList();

    internal static List<GoldBar> Merge(IEnumerable<GoldBar> cached, IEnumerable<GoldBar> fresh) => Clean(cached.Concat(fresh));

    internal static List<GoldBar> ReadCache(string path)
    {
        try { return File.Exists(path) ? Clean(File.ReadLines(path).Select(ParseBar).OfType<GoldBar>()) : new List<GoldBar>(); }
        catch { return new List<GoldBar>(); }
    }

    internal static void WriteCache(string path, IReadOnlyList<GoldBar> bars)
    {
        var text = new StringBuilder("date,open,close,high,low\r\n");
        foreach (var bar in bars)
            text.Append(bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(Number(bar.Open)).Append(',')
                .Append(Number(bar.Close)).Append(',').Append(Number(bar.High)).Append(',').Append(Number(bar.Low)).Append("\r\n");
        string temp = path + ".tmp";
        File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // basePrice: the live quote when there is one, otherwise (0) the last close. Horizons count closes after the last bar;
    // while that bar is still trading, about half a day more of movement is still to come and is added to every horizon.
    internal static GoldForecast? Compute(string code, IReadOnlyList<GoldBar> bars, double basePrice, bool liveBase, DateTime now)
    {
        int n = bars.Count;
        if (n < MinimumDays) return null;
        var closes = bars.Select(b => b.Close).ToArray();
        if (!double.IsFinite(basePrice) || basePrice <= 0) { basePrice = closes[^1]; liveBase = false; }
        var returns = new double[n - 1];
        for (int i = 1; i < n; i++) returns[i - 1] = Math.Log(closes[i] / closes[i - 1]);

        double variance = 0;
        for (int i = 0; i < SeedDays; i++) variance += returns[i] * returns[i];
        variance /= SeedDays;
        for (int i = SeedDays; i < returns.Length; i++) variance = Lambda * variance + (1 - Lambda) * returns[i] * returns[i];

        int window = Math.Min(LongWindow, returns.Length);
        double mean = 0, longVariance = 0;
        for (int i = returns.Length - window; i < returns.Length; i++) mean += returns[i];
        mean /= window;
        for (int i = returns.Length - window; i < returns.Length; i++) longVariance += (returns[i] - mean) * (returns[i] - mean);
        longVariance /= window - 1;

        double drift = DriftShrink * returns.Average();
        double extra = Final(bars[^1], now) ? 0 : 0.5;
        var rows = new List<GoldForecastRow>();
        foreach (var (label, days) in Horizons)
        {
            double h = days + extra;
            // average daily variance over the horizon while today's level fades into the long-run one
            double average = longVariance + (variance - longVariance) * (1 - Math.Pow(Persistence, h)) / (h * (1 - Persistence));
            double trend = h * DriftUncertainty / YearDays;
            double sigma = Math.Sqrt(Math.Max(average, 1e-10) * h + trend * trend);
            double center = basePrice * Math.Exp(drift * h);
            int up = 0, total = 0;
            for (int t = 0; t + days < n; t++) { total++; if (closes[t + days] > closes[t]) up++; }
            rows.Add(new GoldForecastRow(label, days, center, center * Math.Exp(-Z68 * sigma), center * Math.Exp(Z68 * sigma),
                center * Math.Exp(-Z95 * sigma), center * Math.Exp(Z95 * sigma), total > 0 ? (double)up / total : double.NaN, sigma));
        }
        return new GoldForecast(code, basePrice, liveBase, bars[^1].Date, rows, Math.Sqrt(variance), Math.Sqrt(longVariance),
            Math.Exp(drift * YearDays) - 1, Trend(closes, basePrice), n, bars[0].Date);
    }

    // Where the price stands against its 20 / 60 / 250-day averages, the last month's move and the distance to the 1-year high.
    internal static string Trend(IReadOnlyList<double> closes, double price)
    {
        int n = closes.Count;
        var above = new List<int>();
        var below = new List<int>();
        foreach (int days in new[] { 20, 60, 250 })
        {
            if (n < days) continue;
            double average = 0;
            for (int i = n - days; i < n; i++) average += closes[i];
            (price >= average / days ? above : below).Add(days);
        }
        var text = new StringBuilder();
        if (above.Count + below.Count > 0)
        {
            text.Append("现价");
            if (above.Count > 0) text.Append("在 ").Append(string.Join("/", above)).Append(" 日均线之上");
            if (above.Count > 0 && below.Count > 0) text.Append("、");
            if (below.Count > 0) text.Append("在 ").Append(string.Join("/", below)).Append(" 日均线之下");
            // descriptive only: where the price is, not where it will go
            string mood = below.Count == 0 ? "高于近一月、一季、一年的均价"
                : above.Count == 0 ? "低于近一月、一季、一年的均价"
                : below.Contains(20) && above.Contains(250) ? "近期回落，但仍高于一年均价"
                : above.Contains(20) && below.Contains(250) ? "近期回升，但仍低于一年均价"
                : "各均线方向不一致";
            text.Append("（").Append(mood).Append("）");
        }
        if (n > 20)
        {
            if (text.Length > 0) text.Append("；");
            text.Append("近 20 个交易日 ").Append(((price / closes[n - 21] - 1) * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)).Append('%');
        }
        double high = price;
        for (int i = Math.Max(0, n - 250); i < n; i++) high = Math.Max(high, closes[i]);
        double drop = (1 - price / high) * 100;
        if (text.Length > 0) text.Append("；");
        text.Append(drop < 1 ? "在一年内的高位附近" : $"比一年内最高 {Price(high)} 低 {drop.ToString("0.0", CultureInfo.InvariantCulture)}%");
        return text.Append('。').ToString();
    }

    internal static string Price(double value) => value.ToString(value >= 2000 ? "0" : "0.0", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ today's session
    // SGE trading day: night 20:00–2:30 (belongs to the next trading day), day 9:00–11:30 and 13:30–15:30 — 11 hours in all.
    internal const double SessionHours = 11;

    // The next close and how many trading hours are left until it (public holidays are not known here).
    internal static (string Label, double Hours) NextClose(DateTime now)
    {
        double h = now.TimeOfDay.TotalHours;
        var day = now.DayOfWeek;
        bool weekday = day is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        const double daySession = 4.5;
        if (h < 2.5 && day is >= DayOfWeek.Tuesday and <= DayOfWeek.Saturday) // last night's session is still running
            return (day == DayOfWeek.Saturday ? "下周一收盘" : "今天收盘", 2.5 - h + daySession);
        if (!weekday) return ("下周一收盘", SessionHours);
        if (h < 9) return ("今天收盘", daySession);
        if (h < 11.5) return ("今天收盘", 11.5 - h + 2);
        if (h < 13.5) return ("今天收盘", 2);
        if (h < 15.5) return ("今天收盘", 15.5 - h);
        string next = day == DayOfWeek.Friday ? "下周一收盘" : "明天收盘";
        return h < 20 ? (next, SessionHours) : (next, 24 - h + 2.5 + daySession);
    }

    // The range for the next close around the price now: today's volatility scaled to the trading hours left, no trend.
    internal static GoldForecastRow Session(GoldForecast forecast, double price, DateTime now)
    {
        var (label, hours) = NextClose(now);
        if (!(price > 0)) price = forecast.Base;
        double sigma = forecast.DailyVolatility * Math.Sqrt(Math.Max(hours, .25) / SessionHours);
        return new GoldForecastRow(label, 0, price, price * Math.Exp(-Z68 * sigma), price * Math.Exp(Z68 * sigma),
            price * Math.Exp(-Z95 * sigma), price * Math.Exp(Z95 * sigma), double.NaN, sigma);
    }

    internal static string Unit(string code) => code.StartsWith("AG", StringComparison.Ordinal) ? "元/千克" : "元/克";

    // ------------------------------------------------------------------ the record of past forecasts
    internal const string RecordHeader = "预测时间,品种,基准日,交易日数,基准价,中间值,68%下限,68%上限,95%下限,95%上限";

    internal static List<GoldForecastRecord> ReadRecords(string path)
    {
        var records = new List<GoldForecastRecord>();
        try
        {
            if (!File.Exists(path)) return records;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var p = line.Split(',');
                if (p.Length < 10 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var made)
                    || !DateTime.TryParseExact(p[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var baseDate)
                    || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days <= 0) continue;
                var v = new double[6];
                bool ok = true;
                for (int i = 0; i < 6 && ok; i++)
                    ok = double.TryParse(p[i + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) && double.IsFinite(v[i]) && v[i] > 0;
                if (ok) records.Add(new GoldForecastRecord(made, p[1], baseDate, days, v[0], v[1], v[2], v[3], v[4], v[5]));
            }
        }
        catch { }
        return records;
    }

    internal static void AppendRecords(string path, IEnumerable<GoldForecastRecord> records)
    {
        string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        foreach (var r in records)
            text.Append(string.Join(",", r.Made.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.Code,
                r.BaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Days.ToString(CultureInfo.InvariantCulture),
                F(r.Base), F(r.Center), F(r.Low68), F(r.High68), F(r.Low95), F(r.High95))).Append("\r\n");
        if (!File.Exists(path)) File.WriteAllText(path, RecordHeader + "\r\n", new UTF8Encoding(true));
        File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    // A bar is final once its day session has closed (15:30); the night session already belongs to the next day's bar.
    internal static bool Final(GoldBar bar, DateTime now) =>
        bar.Date.Date < now.Date || bar.Date.Date == now.Date && now.TimeOfDay >= new TimeSpan(15, 31, 0);

    // How past forecasts did: for each horizon, how many have come due and how many closes landed inside the ranges.
    internal static List<GoldForecastScore> Score(IEnumerable<GoldForecastRecord> records, string code, IReadOnlyList<GoldBar> bars, DateTime now)
    {
        var index = new Dictionary<DateTime, int>();
        for (int i = 0; i < bars.Count; i++) index[bars[i].Date.Date] = i;
        var mine = records.Where(r => r.Code == code).ToList();
        var scores = new List<GoldForecastScore>();
        foreach (var (_, days) in Horizons)
        {
            int recorded = 0, done = 0, in68 = 0, in95 = 0;
            double miss = 0;
            foreach (var r in mine.Where(r => r.Days == days))
            {
                recorded++;
                if (!index.TryGetValue(r.BaseDate.Date, out int i) || i + days >= bars.Count || !Final(bars[i + days], now)) continue;
                double actual = bars[i + days].Close;
                done++;
                if (actual >= r.Low68 && actual <= r.High68) in68++;
                if (actual >= r.Low95 && actual <= r.High95) in95++;
                miss += Math.Abs(actual / r.Center - 1) * 100;
            }
            scores.Add(new GoldForecastScore(days, recorded, done, in68, in95, done > 0 ? miss / done : double.NaN));
        }
        return scores;
    }
}
