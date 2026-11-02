using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BigBlueFish.Windows;

internal sealed record GoldFactorRow(string Name, string Latest, string Change, string Rule, string Now, int Lean);

// 国内金价 ≈ 国际金价 × 美元兑人民币 ÷ 31.1035 (克/盎司) + 国内溢价; the 20-day columns split the domestic move into its parts.
internal sealed record GoldDecomposition(double International, double Fx, double Implied, double Domestic, double Premium,
    double InternationalChange, double FxChange, double ImpliedChange, double DomesticChange, double PremiumChange, DateTime Since);

internal sealed record GoldLevel(string Label, double High, DateTime HighDate, double Low, DateTime LowDate, double Average, double Position);

// The things that move the domestic gold price, read from daily K-lines: international gold, the Fed (2-year Treasury yield),
// the dollar index, risk appetite (S&P 500), central-bank buying (monthly, not available live), the yuan and the product spread.
// They describe what is happening and are shown next to the forecast; a back-test (2011–2026) found their 20-day moves explain
// the domestic move of the same days well (correlation ≈ 0.95 for international gold + yuan) but say little about the next
// month (|correlation| < 0.1), so the forecast ranges are not tilted by them.
internal static class CompanionGoldFactors
{
    internal const double GramsPerOunce = 31.1035;
    internal const int Lookback = 20;

    internal static readonly (string Key, string Secid)[] Series =
    [
        ("XAU", "122.XAU"), ("XAG", "122.XAG"), ("UDI", "100.UDI"), ("US2Y", "171.US2Y"), ("US10Y", "171.US10Y"),
        ("SPX", "100.SPX"), ("CNH", "133.USDCNH"), ("CNYC", "120.USDCNYC")
    ];

    // The international contract behind a domestic variety (none for platinum: no free daily source).
    internal static string? International(string code) =>
        code.StartsWith("AG", StringComparison.Ordinal) ? "XAG" : code.StartsWith("PT", StringComparison.Ordinal) ? null : "XAU";

    private static double Units(string code) => code.StartsWith("AG", StringComparison.Ordinal) ? 1000 : 1; // 元/千克 vs 元/克

    internal static string KlineUrl(string secid, int limit) =>
        $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55&klt=101&fqt=0&end=20500101&lmt={limit}";

    private static string N(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static List<GoldBar>? Get(IReadOnlyDictionary<string, List<GoldBar>> series, string? key) =>
        key != null && series.TryGetValue(key, out var bars) && bars.Count > Lookback ? bars : null;

    // The last close on or before a date.
    internal static double AsOf(IReadOnlyList<GoldBar> bars, DateTime date)
    {
        double value = double.NaN;
        foreach (var bar in bars)
        {
            if (bar.Date.Date > date.Date) break;
            value = bar.Close;
        }
        return value;
    }

    private static (double Latest, double Percent, double Points) Move(List<GoldBar> bars)
    {
        double latest = bars[^1].Close, before = bars[^(Lookback + 1)].Close;
        return (latest, (latest / before - 1) * 100, latest - before);
    }

    private static string Signed(double value, string format) => value.ToString("+" + format + ";-" + format + ";" + format, CultureInfo.InvariantCulture);

    internal static List<GoldFactorRow> Rows(string code, IReadOnlyDictionary<string, List<GoldBar>> series, double price, double spread,
        GoldForecast? forecast)
    {
        var rows = new List<GoldFactorRow>();
        const string missing = "暂无数据";

        string? intlKey = International(code);
        string intlName = intlKey == "XAG" ? "国际银价（伦敦银）" : "国际金价（伦敦金）";
        if (intlKey == null)
            rows.Add(new GoldFactorRow("国际铂金价", missing, "—", "国内价格跟随国际价格", "没有免费的国际铂金日线，鱼鱼看不到", 0));
        else if (Get(series, intlKey) is { } intl)
        {
            var (latest, percent, _) = Move(intl);
            int lean = percent >= .5 ? 1 : percent <= -.5 ? -1 : 0;
            rows.Add(new GoldFactorRow(intlName, N(latest, "0.00") + " 美元/盎司", Signed(percent, "0.00") + "%",
                "国内价格的主要来源；美联储、美元、避险、央行购金都先作用在国际价格上",
                lean > 0 ? "上涨，带动国内价格" : lean < 0 ? "下跌，拖累国内价格" : "基本持平", lean));
        }
        else rows.Add(new GoldFactorRow(intlName, missing, "—", "国内价格的主要来源", "等数据下载好再看", 0));

        if (Get(series, "US2Y") is { } us2)
        {
            var (latest, _, points) = Move(us2);
            string ten = Get(series, "US10Y") is { } us10 ? $" · 10 年 {N(us10[^1].Close, "0.00")}%" : "";
            int lean = points <= -.10 ? 1 : points >= .10 ? -1 : 0;
            rows.Add(new GoldFactorRow("美联储预期（美债 2 年收益率）", $"2 年 {N(latest, "0.00")}%{ten}", Signed(points * 100, "0") + " 个基点",
                "降息预期升温、收益率下行时，持有黄金的机会成本降低，通常利好金价；反之是压力",
                lean > 0 ? "收益率下行，通常是支撑" : lean < 0 ? "收益率上行，通常是压力" : "变化不大", lean));
        }
        else rows.Add(new GoldFactorRow("美联储预期（美债 2 年收益率）", missing, "—", "降息预期升温通常利好金价", "等数据下载好再看", 0));

        if (Get(series, "UDI") is { } udi)
        {
            var (latest, percent, _) = Move(udi);
            int lean = percent >= .5 ? -1 : percent <= -.5 ? 1 : 0;
            rows.Add(new GoldFactorRow("美元指数", N(latest, "0.00"), Signed(percent, "0.00") + "%",
                "黄金以美元计价，美元走强时金价通常承压，走弱时通常受支撑",
                lean < 0 ? "美元走强，通常是压力" : lean > 0 ? "美元走弱，通常是支撑" : "基本持平", lean));
        }
        else rows.Add(new GoldFactorRow("美元指数", missing, "—", "美元走强时金价通常承压", "等数据下载好再看", 0));

        if (Get(series, "SPX") is { } spx)
        {
            var (latest, percent, _) = Move(spx);
            int lean = percent <= -3 ? 1 : percent >= 3 ? -1 : 0;
            rows.Add(new GoldFactorRow("避险情绪（标普 500）", N(latest, "0"), Signed(percent, "0.00") + "%",
                "股市大跌、地缘或金融风险升温时，资金常流向黄金避险",
                lean > 0 ? "股市明显回落，避险需求上升" : lean < 0 ? "股市走强，避险需求偏弱" : "情绪平稳", lean));
        }
        else rows.Add(new GoldFactorRow("避险情绪（标普 500）", missing, "—", "风险升温时资金常流向黄金", "等数据下载好再看", 0));

        rows.Add(new GoldFactorRow("央行购金", "按月公布", "—", "各国央行持续买入是金价的长期支撑", "没有实时数据，只作长期背景", 0));

        if (Get(series, "CNH") is { } cnh)
        {
            var (latest, percent, _) = Move(cnh);
            string mid = Get(series, "CNYC") is { } cnyc ? $"（中间价 {N(cnyc[^1].Close, "0.0000")}）" : "";
            int lean = percent >= .3 ? 1 : percent <= -.3 ? -1 : 0;
            rows.Add(new GoldFactorRow("人民币汇率（美元兑离岸人民币）", N(latest, "0.0000") + mid, Signed(percent, "0.00") + "%",
                "人民币贬值时国内金价被动上涨；人民币升值时压低国内金价",
                lean > 0 ? $"人民币贬值 {N(percent, "0.0")}%，推高国内价格" : lean < 0 ? $"人民币升值 {N(-percent, "0.0")}%，压低国内价格" : "基本稳定", lean));
        }
        else rows.Add(new GoldFactorRow("人民币汇率（美元兑离岸人民币）", missing, "—", "人民币贬值推高国内金价，升值压价", "等数据下载好再看", 0));

        string unit = CompanionGoldForecast.Unit(code);
        if (spread > 0 && price > 0)
        {
            string now = "买卖一次的成本";
            if (forecast?.Row(1) is { } day && forecast.Row(5) is { } week)
            {
                double swing = (day.High68 - day.Low68) / 2;
                now = $"约等于下个交易日典型波动（±{CompanionGoldForecast.Price(swing)}）的 {N(spread / swing * 100, "0")}%；"
                    + $"一周后高于回本价的统计概率约 {N(ChanceAbove(week, price, price + spread) * 100, "0")}%（不计趋势）";
            }
            rows.Add(new GoldFactorRow("产品点差", $"{CompanionGoldForecast.Price(spread)} {unit}（{N(spread / price * 100, "0.00")}%）", "—",
                "买卖有成本：每买卖一次都要付一次点差，交易越频繁，累计成本越高", now, 0));
        }
        else rows.Add(new GoldFactorRow("产品点差", "未填写", "—", "买卖有成本：每买卖一次都要付一次点差，交易越频繁，累计成本越高", "在下面「点差成本」里填你产品的买入价与回购价之差", 0));
        return rows;
    }

    // One line for chat: the 20-day moves of the main factors.
    internal static string Brief(string code, IReadOnlyDictionary<string, List<GoldBar>> series)
    {
        var parts = new List<string>();
        if (Get(series, International(code)) is { } intl)
            parts.Add((International(code) == "XAG" ? "国际银价 " : "国际金价 ") + Signed(Move(intl).Percent, "0.0") + "%");
        if (Get(series, "UDI") is { } udi) parts.Add("美元指数 " + Signed(Move(udi).Percent, "0.0") + "%");
        if (Get(series, "US2Y") is { } us2) parts.Add("美债 2 年收益率 " + Signed(Move(us2).Points * 100, "0") + " 个基点");
        if (Get(series, "CNH") is { } cnh)
        {
            double percent = Move(cnh).Percent;
            parts.Add(Math.Abs(percent) < .3 ? "人民币汇率基本稳定" : percent > 0 ? $"人民币贬值 {N(percent, "0.0")}%" : $"人民币升值 {N(-percent, "0.0")}%");
        }
        return parts.Count == 0 ? "" : "近 20 个交易日：" + string.Join("，", parts) + "。";
    }

    internal static GoldDecomposition? Decompose(string code, IReadOnlyList<GoldBar> domestic, double price,
        IReadOnlyDictionary<string, List<GoldBar>> series)
    {
        if (Get(series, International(code)) is not { } intl || Get(series, "CNH") is not { } cnh || domestic.Count <= Lookback || !(price > 0))
            return null;
        double units = Units(code);
        var since = domestic[^(Lookback + 1)];
        double intl0 = AsOf(intl, since.Date), fx0 = AsOf(cnh, since.Date);
        if (!(intl0 > 0) || !(fx0 > 0)) return null;
        double intlNow = intl[^1].Close, fxNow = cnh[^1].Close;
        double implied = intlNow * fxNow / GramsPerOunce * units, implied0 = intl0 * fx0 / GramsPerOunce * units;
        double premium = price - implied, premium0 = since.Close - implied0;
        return new GoldDecomposition(intlNow, fxNow, implied, price, premium,
            (intlNow / intl0 - 1) * 100, (fxNow / fx0 - 1) * 100, (implied / implied0 - 1) * 100, (price / since.Close - 1) * 100,
            premium - premium0, since.Date);
    }

    // Highest / lowest (with their days), average and where the price sits in that range (0 % = the low, 100 % = the high).
    internal static List<GoldLevel> Levels(IReadOnlyList<GoldBar> bars, double price)
    {
        var levels = new List<GoldLevel>();
        if (bars.Count == 0) return levels;
        foreach (var (label, days) in new[] { ("近 20 个交易日", 20), ("近 60 个交易日", 60), ("近一年（250 个交易日）", 250), ("全部历史", int.MaxValue) })
        {
            if (days != int.MaxValue && bars.Count < days) continue;
            var span = days == int.MaxValue ? bars.ToList() : bars.Skip(bars.Count - days).ToList();
            string name = days == int.MaxValue ? $"{span[0].Date:yyyy} 年以来" : label;
            var high = span.MaxBy(b => b.High > 0 ? b.High : b.Close)!;
            var low = span.MinBy(b => b.Low > 0 ? b.Low : b.Close)!;
            double h = high.High > 0 ? high.High : high.Close, l = low.Low > 0 ? low.Low : low.Close;
            // Position can leave 0–100 % when the live price breaks out of the historical range; the panel says so.
            levels.Add(new GoldLevel(name, h, high.Date, l, low.Date, span.Average(b => b.Close), h > l ? (price - l) / (h - l) * 100 : 50));
        }
        return levels;
    }

    // Standard normal CDF (Abramowitz–Stegun 7.1.26, error < 1.5e-7).
    internal static double Phi(double x)
    {
        double z = Math.Abs(x) / Math.Sqrt(2), t = 1 / (1 + 0.3275911 * z);
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    // Chance that the close at the end of the horizon is at or above a level: the horizon's spread around today's price, with no
    // trend at all (so it neither leans towards rising nor falling).
    internal static double ChanceAbove(GoldForecastRow row, double basePrice, double level) =>
        level <= 0 ? 1 : basePrice <= 0 ? 0 : row.Sigma <= 0 ? (basePrice >= level ? 1 : 0) : 1 - Phi(Math.Log(level / basePrice) / row.Sigma);

    // Chance that the price reaches a level at some point within the horizon (reflection principle, trend ignored).
    internal static double ChanceTouch(GoldForecastRow row, double basePrice, double level)
    {
        if (level <= 0 || basePrice <= 0) return 0;
        double distance = Math.Abs(Math.Log(level / basePrice));
        if (distance < 1e-9) return 1;
        return row.Sigma <= 0 ? 0 : Math.Min(1, 2 * (1 - Phi(distance / row.Sigma)));
    }
}
