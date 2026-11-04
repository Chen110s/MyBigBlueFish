using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// 招行金价面板 (right-click 「招行金价」): everything about the watched variety in tables — live quotes, the trend chart,
// the day / week / month / year ranges, the factors behind the price, how the domestic price is made up, the cost of the
// spread, the owner's own planned prices and historical reference levels. Facts and statistics only; no buy / sell calls.
public partial class MainWindow
{
    private BigBlueFish.Deployment.ModernWindow? goldPanel;
    private Action? goldPanelView;
    private List<GoldQuote> goldAllQuotes = new();
    private readonly Dictionary<string, List<GoldBar>> goldFactorBars = new();
    private DateTime goldFactorsFetched = DateTime.MinValue, nextGoldFactorTry = DateTime.MinValue;
    private Task? goldFactorTask;
    private string goldFactorStatus = "判断因素还没有下载。";
    private string goldChartRange = "3M";
    private List<(DateTime Time, double Price)> goldWeekRecords = new();
    private string goldWeekKey = "";

    private static readonly Brush GoldInk = Frozen(38, 51, 76), GoldGridLine = Frozen(232, 236, 244), GoldHeaderBack = Frozen(241, 245, 251),
        GoldStripe = Frozen(250, 251, 253), GoldHighlight = Frozen(255, 247, 222), GoldUp = Frozen(214, 64, 64), GoldDown = Frozen(32, 145, 84),
        GoldMuted = Frozen(112, 128, 150), GoldAccent = Frozen(80, 96, 152), GoldFan = Frozen(80, 120, 200, 40), GoldMa = Frozen(140, 160, 205),
        GoldPlanBuy = Frozen(40, 120, 200), GoldPlanSell = Frozen(230, 140, 30);

    private static Brush Frozen(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record GoldCell(string Text, Brush? Brush = null, bool Strong = false);

    private void NotifyGoldViews()
    {
        goldForecastView?.Invoke();
        goldPanelView?.Invoke();
    }

    // ---------------------------------------------------------------- factor data (EastMoney daily K-lines, last 80 days)
    private void MaybeRefreshGoldFactors()
    {
        bool missing = CompanionGoldFactors.International(preferences.GoldVariety) is { } international && !goldFactorBars.ContainsKey(international);
        if (!goldOffline && (missing || DateTime.UtcNow - goldFactorsFetched > TimeSpan.FromMinutes(30)) && DateTime.UtcNow >= nextGoldFactorTry)
            _ = RefreshGoldFactors();
    }

    private Task RefreshGoldFactors() =>
        goldFactorTask is { IsCompleted: false } running ? running : goldFactorTask = RefreshGoldFactorsCore();

    private async Task RefreshGoldFactorsCore()
    {
        if (goldOffline) return;
        var web = GoldHistoryWeb();
        using var gate = new System.Threading.SemaphoreSlim(3); // be gentle with the server
        async Task<(string Key, List<GoldBar> Bars, string? Error)> One((string Key, string Secid) series)
        {
            await gate.WaitAsync();
            try { return (series.Key, CompanionGoldForecast.ParseHistory(await web.GetStringAsync(CompanionGoldFactors.KlineUrl(series.Secid, 80))), null); }
            catch (Exception ex) { return (series.Key, new List<GoldBar>(), ex is TaskCanceledException ? "连接超时" : ex.Message); }
            finally { gate.Release(); }
        }
        string? international = CompanionGoldFactors.International(preferences.GoldVariety);
        var wanted = CompanionGoldFactors.Series.Where(s => s.Key is not ("XAU" or "XAG") || s.Key == international).ToList();
        var results = await Task.WhenAll(wanted.Select(One));
        int ok = 0;
        string? error = null;
        foreach (var (key, bars, problem) in results)
        {
            if (bars.Count > 0) { goldFactorBars[key] = bars; ok++; }
            else error ??= problem ?? "没有返回数据";
        }
        if (ok > 0) goldFactorsFetched = DateTime.UtcNow;
        else nextGoldFactorTry = DateTime.UtcNow.AddMinutes(10);
        goldFactorStatus = ok == results.Length ? $"{DateTime.Now:HH:mm} 判断因素已更新。"
            : ok > 0 ? $"{DateTime.Now:HH:mm} 判断因素部分更新（{ok}/{results.Length}）：{error}"
            : $"{DateTime.Now:HH:mm} 判断因素获取失败：{error}";
        NotifyGoldViews();
    }

    // CMB prices recorded in gold-records.csv for the last days (the 7-day chart).
    private List<(DateTime Time, double Price)> GoldRecordsSince(DateTime from)
    {
        var points = new List<(DateTime, double)>();
        try
        {
            if (!File.Exists(GoldRecordFile)) return points;
            string variety = GoldName.Split('（')[0];
            foreach (var line in File.ReadLines(GoldRecordFile, Encoding.UTF8).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 4 || parts[2] != variety) continue;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    || time < from || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price <= 0) continue;
                points.Add((time, price));
            }
        }
        catch { }
        return points;
    }

    // ---------------------------------------------------------------- the window
    private void ShowGoldPanel()
    {
        if (companionClosed || exitAnimation || exitReady) return;
        if (goldPanel != null)
        {
            if (goldPanel.WindowState == WindowState.Minimized) goldPanel.WindowState = WindowState.Normal;
            goldPanel.Show();
            goldPanel.Activate();
            return;
        }
        var window = new BigBlueFish.Deployment.ModernWindow("招行金价面板 · 蓝色大肥鱼", "招行金价面板",
            "实时行情来自招商银行一网通；日线、国际金价、美元、美债、标普 500 和人民币汇率来自东方财富。"
            + "下面是数据整理和统计推算，帮你自己判断，不构成投资建议；买不买、什么时候买由你决定。", 960, 940);
        window.ShowInTaskbar = true;
        window.MinWidth = Math.Min(900, SystemParameters.WorkArea.Width - 32);
        var body = window.Body;
        var boxes = new List<Action>(); // refresh / commit of the number boxes

        Border Section(string title, string? note = null)
        {
            body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = GoldInk, Margin = new Thickness(0, 18, 0, 4) });
            if (note != null)
                body.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = GoldMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var card = new Border { Background = Brushes.White, BorderBrush = GoldGridLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 0, 0, 2), SnapsToDevicePixels = true };
            body.Children.Add(card);
            return card;
        }
        TextBlock Note()
        {
            var text = new TextBlock { FontSize = 12, Foreground = GoldMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
            body.Children.Add(text);
            return text;
        }
        StackPanel InputRow()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(row);
            return row;
        }
        // A box only writes when its text was really edited, so a stale box (the same value is also editable in settings)
        // never puts an old value back, and an unchanged plan price does not re-arm its alert.
        TextBox NumberBox(Func<double> current, Action<double> change)
        {
            static string Format(double v) => v > 0 ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";
            string shown = Format(current());
            var box = new TextBox { Width = 96, Text = shown, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0) };
            void Commit()
            {
                if (box.Text.Trim() == shown) return;
                double parsed = double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && v < 1_000_000 ? v : 0;
                box.Text = shown = Format(parsed);
                if (parsed == current()) return;
                change(parsed);
                SaveCompanion();
                goldPanelView?.Invoke();
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
            boxes.Add(() => { if (!box.IsKeyboardFocusWithin && box.Text.Trim() == shown) box.Text = shown = Format(current()); });
            window.Closing += (_, _) => Commit();
            return box;
        }
        TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = GoldInk };

        // Headline: the price now and the expected range of the next close, as in the bubble.
        var headline = new TextBlock { FontSize = 15, Foreground = GoldInk, TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
        body.Children.Add(new Border { Background = GoldHighlight, BorderBrush = GoldGridLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 4), Child = headline });
        void Group(string title, string? note = null)
        {
            body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = GoldAccent, Margin = new Thickness(0, 26, 0, 0) });
            if (note != null)
                body.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = GoldMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }

        Group("一、金价变化");
        // 1. live quotes
        var quotes = Section("实时行情（招商银行）");
        var quoteNote = Note();
        // 2. chart
        var chartSection = Section("价格走势");
        var ranges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 4) };
        var chartHost = new Border { Margin = new Thickness(6, 0, 6, 6) };
        chartSection.Child = new StackPanel { Children = { ranges, chartHost } };
        var chartNote = Note();
        var rangeButtons = new List<(string Key, Button Button)>();
        foreach (var (key, label) in new[] { ("D", "今天"), ("W", "近 7 天"), ("1M", "1 个月"), ("3M", "3 个月"), ("1Y", "1 年"), ("3Y", "3 年"), ("ALL", "全部") })
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0, MinWidth = 0, Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
            button.Click += (_, _) =>
            {
                goldChartRange = key;
                RenderChart();
            };
            rangeButtons.Add((key, button));
            ranges.Children.Add(button);
        }
        Group("二、金价预测");
        // 3. forecast
        var forecastCard = Section("今天 / 天 / 周 / 月 / 年推测",
            "按历史波动推算的价格区间：约 68% / 95% 的情况会落在其中。「今天收盘」按今天的波动和剩下的交易时间推算，中间值就是现价；其余周期的中间值只算了长期平均涨幅的一半，不代表看涨或看跌；周期越长越不准。");
        var forecastNote = Note();
        Group("三、买卖参考", "帮你自己做决定的参考数据：你的计划价位、点差回本和历史价位。鱼鱼不做买卖推荐，买不买、什么时候买由你决定。");
        // 7. plan
        var planCard = Section("我的计划价位", "你自己定的价位：鱼鱼帮你盯着，到价提醒，并按波动粗算触及的可能性。买不买由你决定。");
        var planRow = InputRow();
        planRow.Children.Add(Caption("计划买入价（低于时提醒）"));
        planRow.Children.Add(NumberBox(() => preferences.GoldAlertLow, v => { preferences.GoldAlertLow = v; goldLowArmed = true; }));
        planRow.Children.Add(Caption("计划卖出价（高于时提醒）"));
        planRow.Children.Add(NumberBox(() => preferences.GoldAlertHigh, v => { preferences.GoldAlertHigh = v; goldHighArmed = true; }));
        body.Children.Remove(planRow);
        body.Children.Insert(body.Children.IndexOf(planCard), planRow);
        var planNote = Note();
        // 6. spread
        var spreadCard = Section("点差成本与回本", "点差 = 买入价 − 卖出（回购）价。假设按现价买入、到时按市场价卖出，价格要高过「现价 + 点差」才能覆盖这笔成本。下面的概率只看波动、不计任何趋势。");
        var spreadRow = InputRow();
        spreadRow.Children.Add(Caption("我的产品点差"));
        spreadRow.Children.Add(NumberBox(() => preferences.GoldSpread, v => preferences.GoldSpread = v));
        var spreadUnit = Caption("");
        spreadRow.Children.Add(spreadUnit);
        spreadRow.Children.Add(new TextBlock { Text = "（在招行 App 里看到买入 840、回购 836，就填 4）", Foreground = GoldMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        body.Children.Remove(spreadRow);
        body.Children.Insert(body.Children.IndexOf(spreadCard), spreadRow);
        // 8. levels
        var levelCard = Section("历史参考价位", "按日线最高价、最低价统计，只做描述：现价位置 0% = 区间最低，100% = 区间最高。");

        Group("四、判断依据", "国内金价的方向主要来自国际金价，再经人民币汇率换算。");
        // 4. factors
        var factorCard = Section("判断因素", "国内金价的方向主要来自国际金价（受美联储、美元、避险、央行购金影响），再经人民币汇率换算；买卖还要算上产品点差。");
        var factorNote = Note();
        // 5. decomposition
        var splitCard = Section("国内金价是怎么来的", "国内金价 ≈ 国际金价（美元/盎司）× 美元兑人民币 ÷ 31.1035（克/盎司）+ 国内溢价");
        var splitNote = Note();
        void RenderChart()
        {
            foreach (var (key, button) in rangeButtons)
            {
                bool on = key == goldChartRange;
                button.Background = on ? GoldAccent : Brushes.White;
                button.Foreground = on ? Brushes.White : GoldInk;
            }
            var (points, daily, note) = GoldChartPoints();
            // Scales down instead of being cut off when the window is narrower than the chart.
            chartHost.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Child = BuildGoldTrend(points, daily, daily ? goldForecast : null, preferences.GoldAlertLow, preferences.GoldAlertHigh,
                daily ? GoldMovingAverage(points.Count) : null) };
            chartNote.Text = note;
        }

        void Render()
        {
            UpdateGoldForecast();
            var f = goldForecast;
            string code = preferences.GoldVariety, unit = CompanionGoldForecast.Unit(code);
            double price = goldQuote is { } q && q.Code == code ? q.Price : f?.Base ?? (goldBars.Count > 0 ? goldBars[^1].Close : 0);
            string P(double v) => CompanionGoldForecast.Price(v);
            string Pct(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
            Brush Tone(double v) => v > 0 ? GoldUp : v < 0 ? GoldDown : GoldInk;
            spreadUnit.Text = unit;
            headline.Text = GoldTodayText(true);
            foreach (var refreshBox in boxes) refreshBox();

            // 1. quotes
            var rows = new List<IReadOnlyList<GoldCell>>();
            int selected = -1;
            foreach (var (vc, _) in CompanionGoldFeed.Varieties)
            {
                if (goldAllQuotes.FirstOrDefault(x => x.Code == vc) is not { } x) continue;
                if (vc == code) selected = rows.Count;
                rows.Add(new GoldCell[] { new(x.Variety + (vc == code ? "（鱼鱼在盯）" : ""), Strong: vc == code), new(x.Price.ToString("0.00", CultureInfo.InvariantCulture), Tone(x.UpDown), true),
                    new(x.UpDown.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture), Tone(x.UpDown)), new(Pct(x.Percent), Tone(x.UpDown)),
                    new(x.Open.ToString("0.00", CultureInfo.InvariantCulture)), new(x.PreClose.ToString("0.00", CultureInfo.InvariantCulture)),
                    new(x.High.ToString("0.00", CultureInfo.InvariantCulture)), new(x.Low.ToString("0.00", CultureInfo.InvariantCulture)) });
            }
            if (rows.Count == 0) rows.Add(new GoldCell[] { new(goldOffline ? "测试模式下不联网" : "正在获取招行行情……", GoldMuted) });
            quotes.Child = GoldTable(new[] { "品种", "最新价", "涨跌", "涨跌幅", "开盘", "昨收", "最高", "最低" }, new double[] { 190, 100, 90, 90, 90, 90, 90, 0 }, rows, selected);
            quoteNote.Text = (goldSnapshot.Length > 0 ? $"行情时间 {goldSnapshot}。" : "") + goldStatus + " 黄金、铂金单位 元/克，白银 元/千克。"
                + (CompanionGoldFeed.MarketOpen(DateTime.Now) ? "" : " 现在休市，显示的是最近的价格。");

            // 3. forecast
            if (f == null)
                forecastCard.Child = GoldTable(new[] { "推测" }, new double[] { 0 }, new[] { new GoldCell[] { new(goldHistoryLoading ? "正在读取历史日线……" : goldOffline ? "测试模式下不联网。" : "还没有足够的历史日线（至少 300 个交易日）。", GoldMuted) } });
            else
                forecastCard.Child = GoldTable(new[] { "周期", "中间值", "68% 的情况落在", "95% 的情况落在", "历史上同周期上涨占比" }, new double[] { 130, 120, 190, 190, 0 },
                    (price > 0 ? new[] { CompanionGoldForecast.Session(f, price, DateTime.Now) } : Array.Empty<GoldForecastRow>()).Concat(f.Rows).Select(r => (IReadOnlyList<GoldCell>)new GoldCell[] { new(r.Label, Strong: true), new(P(r.Center)), new($"{P(r.Low68)} ～ {P(r.High68)}", Strong: true),
                        new($"{P(r.Low95)} ～ {P(r.High95)}", GoldMuted), new(double.IsFinite(r.UpShare) ? $"{r.UpShare * 100:0}%" : "—") }));
            forecastNote.Text = f == null ? goldHistoryStatus
                : $"基准：{(f.LiveBase ? "招行现价" : $"{f.BaseDate.Month} 月 {f.BaseDate.Day} 日收盘")} {P(f.Base)} {unit}；日波动 {f.DailyVolatility * 100:0.00}%（近 3 年平均 {f.LongVolatility * 100:0.00}%）。"
                    + f.Trend + "\n" + GoldForecastScoreText() + " " + goldHistoryStatus;

            // 4. factors
            var factors = CompanionGoldFactors.Rows(code, goldFactorBars, price, preferences.GoldSpread, f);
            factorCard.Child = GoldTable(new[] { "因素", "最新", "近 20 个交易日", "一般规律（经验）", "近 20 日的作用（按经验）" }, new double[] { 170, 170, 110, 0, 210 },
                factors.Select(r => (IReadOnlyList<GoldCell>)new GoldCell[] { new(r.Name, Strong: true), new(r.Latest), new(r.Change), new(r.Rule, GoldMuted),
                    new((r.Lean > 0 ? "↑ " : r.Lean < 0 ? "↓ " : "· ") + r.Now, r.Lean > 0 ? GoldUp : r.Lean < 0 ? GoldDown : GoldInk) }));
            factorNote.Text = "↑ / ↓ 表示按常见经验，这个因素近 20 个交易日对国内金价起的是支撑还是压力作用，描述的是已经发生的情况。"
                + "回测 2011–2026 年：这些因素很能解释已经发生的涨跌（国内金价 20 日涨跌与「国际金价 + 汇率」的相关约 0.95），"
                + "但对下个月涨跌几乎没有预测力（相关不到 0.1），所以上面的推测区间不按它们调整，只作判断参考。 " + goldFactorStatus;

            // 5. decomposition
            if (CompanionGoldFactors.Decompose(code, goldBars, price, goldFactorBars) is { } d)
            {
                string intlName = code.StartsWith("AG", StringComparison.Ordinal) ? "国际银价（美元/盎司）" : "国际金价（美元/盎司）";
                splitCard.Child = GoldTable(new[] { "项目", "现在", $"近 20 个交易日（自 {d.Since.Month} 月 {d.Since.Day} 日）" }, new double[] { 330, 200, 0 }, new[]
                {
                    new GoldCell[] { new(intlName), new(d.International.ToString("0.00", CultureInfo.InvariantCulture)), new(Pct(d.InternationalChange), Tone(d.InternationalChange)) },
                    new GoldCell[] { new("× 美元兑离岸人民币"), new(d.Fx.ToString("0.0000", CultureInfo.InvariantCulture)),
                        new(Pct(d.FxChange) + (d.FxChange > .05 ? "（人民币贬值）" : d.FxChange < -.05 ? "（人民币升值）" : ""), Tone(d.FxChange)) },
                    new GoldCell[] { new("= 国际价折合人民币（÷ 31.1035）"), new($"{P(d.Implied)} {unit}"), new(Pct(d.ImpliedChange), Tone(d.ImpliedChange)) },
                    new GoldCell[] { new($"招行 {GoldForecastName}", Strong: true), new($"{P(d.Domestic)} {unit}", Strong: true), new(Pct(d.DomesticChange), Tone(d.DomesticChange), true) },
                    new GoldCell[] { new("国内溢价（国内 − 折合）"), new($"{d.Premium:+0.00;-0.00;0.00} {unit}"), new($"变化 {d.PremiumChange:+0.00;-0.00;0.00} {unit}", Tone(d.PremiumChange)) },
                });
                splitNote.Text = $"近 20 个交易日国内价格 {Pct(d.DomesticChange)}，其中国际价格 {Pct(d.InternationalChange)}、汇率 {Pct(d.FxChange)}，其余是国内溢价的变化。"
                    + "两个市场收盘时间不同，按日期对齐，数字是近似的。";
            }
            else
            {
                splitCard.Child = GoldTable(new[] { "拆解" }, new double[] { 0 }, new[] { new GoldCell[] { new(CompanionGoldFactors.International(code) == null
                    ? "铂金没有免费的国际日线，没法拆解。" : "等国际价格和汇率数据下载好再看。", GoldMuted) } });
                splitNote.Text = "";
            }

            // 6. spread
            double spread = preferences.GoldSpread;
            if (f == null || spread <= 0 || price <= 0)
                spreadCard.Child = GoldTable(new[] { "点差成本" }, new double[] { 0 }, new[] { new GoldCell[] { new(spread <= 0 ? "在上面填好点差后，这里会算出回本价和涨过回本价的统计概率。" : "等推测算好再看。", GoldMuted) } });
            else
                spreadCard.Child = GoldTable(new[] { "周期", "回本价（现价 + 点差）", "到时高于回本价的统计概率（不计趋势）", "典型波动（68% 区间半宽）", "点差占典型波动" },
                    new double[] { 130, 190, 200, 190, 0 },
                    f.Rows.Select(r =>
                    {
                        double swing = (r.High68 - r.Low68) / 2, chance = CompanionGoldFactors.ChanceAbove(r, price, price + spread);
                        return (IReadOnlyList<GoldCell>)new GoldCell[] { new(r.Label, Strong: true), new($"{P(price + spread)} {unit}（+{spread / price * 100:0.00}%）"),
                            new($"{chance * 100:0}%", Strong: true), new($"±{P(swing)}"), new($"{spread / swing * 100:0}%") };
                    }));

            // 7. plan
            var plans = new List<IReadOnlyList<GoldCell>>();
            void Plan(string name, double level, bool buy)
            {
                if (level <= 0) return;
                if (price <= 0)
                {
                    plans.Add(new GoldCell[] { new(name, buy ? GoldPlanBuy : GoldPlanSell, true), new($"{P(level)} {unit}"), new("等行情", GoldMuted), new("—"), new("—"), new("—") });
                    return;
                }
                double distance = level - price;
                string reached = buy ? (price <= level ? "现价已在计划买入价以下" : goldLowArmed ? "到价会提醒" : "已提醒过，回到价位上方后重新生效")
                    : (price >= level ? "现价已在计划卖出价以上" : goldHighArmed ? "到价会提醒" : "已提醒过，回到价位下方后重新生效");
                string Touch(int days) => f?.Row(days) is { } r && price > 0
                    ? ((buy ? price <= level : price >= level) ? "已到" : $"{CompanionGoldFactors.ChanceTouch(r, price, level) * 100:0}%") : "—";
                plans.Add(new GoldCell[] { new(name, buy ? GoldPlanBuy : GoldPlanSell, true), new($"{P(level)} {unit}"),
                    new(price > 0 ? $"{distance:+0.00;-0.00;0.00}（{distance / price * 100:+0.0;-0.0;0.0}%）" : "—", Tone(distance)),
                    new(Touch(5)), new(Touch(21)), new(preferences.GoldWatch ? reached : "金价监测已关闭，不会提醒", GoldMuted) });
            }
            Plan("计划买入", preferences.GoldAlertLow, true);
            Plan("计划卖出", preferences.GoldAlertHigh, false);
            if (plans.Count == 0) plans.Add(new GoldCell[] { new("在上面填计划买入价或卖出价（留空 = 不设）", GoldMuted) });
            planCard.Child = GoldTable(new[] { "计划", "价位", "距现价", "一周内触及（统计）", "一个月内触及（统计）", "提醒" }, new double[] { 110, 140, 170, 140, 150, 0 }, plans);
            var planText = new StringBuilder("触及概率按模型的波动粗算期间内碰到这个价位的可能性（不考虑趋势），只作参考。");
            if (preferences.GoldAlertLow > 0 && preferences.GoldAlertHigh > preferences.GoldAlertLow)
            {
                double gain = preferences.GoldAlertHigh - preferences.GoldAlertLow - Math.Max(0, spread);
                planText.Insert(0, $"如果按计划价买入、再按计划价卖出：差价 {P(preferences.GoldAlertHigh - preferences.GoldAlertLow)}{(spread > 0 ? $"，扣点差后 {gain:+0.00;-0.00;0.00}" : "")} {unit}"
                    + $"（{gain / preferences.GoldAlertLow * 100:+0.0;-0.0;0.0}%）。");
            }
            planNote.Text = planText.ToString();

            // 8. levels
            var levels = CompanionGoldFactors.Levels(goldBars, price);
            levelCard.Child = GoldTable(new[] { "区间", "最高（日期）", "最低（日期）", "均价", "现价位置" }, new double[] { 200, 200, 200, 120, 0 },
                levels.Count == 0 ? new[] { new GoldCell[] { new("等历史日线下载好再看。", GoldMuted) } }
                : levels.Select(l => (IReadOnlyList<GoldCell>)new GoldCell[] { new(l.Label, Strong: true), new($"{P(l.High)}（{l.HighDate:yyyy-MM-dd}）", GoldUp),
                    new($"{P(l.Low)}（{l.LowDate:yyyy-MM-dd}）", GoldDown), new(P(l.Average)),
                    new(l.Position > 100 ? "高于区间最高" : l.Position < 0 ? "低于区间最低" : $"{l.Position:0}%") }));

            RenderChart();
        }

        var refresh = BigBlueFish.Deployment.ModernUI.Button("全部刷新", async () =>
        {
            var tasks = new List<Task> { RefreshGoldFactors() };
            if (!goldFetching) tasks.Add(FetchGold(preferences.GoldWatch));
            if (goldHistoryTask is not { IsCompleted: false }) tasks.Add(RefreshGoldHistory(true));
            await Task.WhenAll(tasks);
            goldPanelView?.Invoke();
        }, true);
        refresh.Margin = new Thickness(0, 0, 8, 0);
        window.Actions.Children.Add(refresh);
        var folder = BigBlueFish.Deployment.ModernUI.Button("打开记录文件夹", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.CurrentDirectory}\"") { UseShellExecute = true }); } catch { }
        });
        folder.Margin = new Thickness(0, 0, 8, 0);
        window.Actions.Children.Add(folder);
        var settings = BigBlueFish.Deployment.ModernUI.Button("打开设置", OpenCompanionSettings);
        settings.Margin = new Thickness(0, 0, 8, 0);
        window.Actions.Children.Add(settings);
        window.Actions.Children.Add(BigBlueFish.Deployment.ModernUI.Button("关闭", window.Close));

        // While the panel is open: quotes keep coming (also with gold watching off), factors every 30 minutes.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) =>
        {
            if (!preferences.GoldWatch && !goldFetching && CompanionGoldFeed.MarketOpen(DateTime.Now)) _ = FetchGold(false);
            MaybeRefreshGoldFactors();
            goldPanelView?.Invoke(); // status, market hours and the other varieties also change without the watched price moving
            if (!goldOffline && GoldHistoryStale && DateTime.UtcNow >= nextGoldHistoryTry) _ = RefreshGoldHistory(false);
        };
        window.Closed += (_, _) =>
        {
            timer.Stop();
            if (goldPanel == window) { goldPanel = null; goldPanelView = null; }
        };
        goldPanel = window;
        goldPanelView = Render;
        Render();
        window.Show();
        window.Activate();
        // The pet's menu restores the previous foreground window after the click; activate again once that is done.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() => { if (goldPanel == window) window.Activate(); }));
        timer.Start();

        if (!goldOffline)
        {
            if (goldQuote == null && !goldFetching) _ = FetchGold(preferences.GoldWatch);
            MaybeRefreshGoldFactors();
            if ((goldBars.Count < CompanionGoldForecast.MinimumDays || GoldHistoryStale) && DateTime.UtcNow >= nextGoldHistoryTry) _ = RefreshGoldHistory(false);
        }
    }

    private static Grid GoldTable(IReadOnlyList<string> headers, IReadOnlyList<double> widths, IEnumerable<IReadOnlyList<GoldCell>> rows, int highlight = -1)
    {
        var grid = new Grid { SnapsToDevicePixels = true };
        foreach (double width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width > 0 ? new GridLength(width) : new GridLength(1, GridUnitType.Star) });
        void Row(IReadOnlyList<GoldCell> cells, int index, Brush background, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var band = new Border { Background = background, BorderBrush = GoldGridLine, BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(band, index);
            Grid.SetColumnSpan(band, widths.Count);
            grid.Children.Add(band);
            for (int c = 0; c < cells.Count && c < widths.Count; c++)
            {
                var cell = new TextBlock { Text = cells[c].Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 7, 8, 7), VerticalAlignment = VerticalAlignment.Center,
                    FontSize = header ? 12 : 13, Foreground = cells[c].Brush ?? (header ? GoldMuted : GoldInk),
                    FontWeight = header || cells[c].Strong ? FontWeights.SemiBold : FontWeights.Normal };
                Grid.SetRow(cell, index);
                Grid.SetColumn(cell, c);
                if (cells.Count == 1) Grid.SetColumnSpan(cell, widths.Count);
                grid.Children.Add(cell);
            }
        }
        Row(headers.Select(h => new GoldCell(h)).ToList(), 0, GoldHeaderBack, true);
        int i = 1;
        foreach (var row in rows)
        {
            Row(row, i, i - 1 == highlight ? GoldHighlight : i % 2 == 0 ? GoldStripe : Brushes.White, false);
            i++;
        }
        return grid;
    }

    // ---------------------------------------------------------------- the trend chart
    // Points for the chosen range: CMB's own recorded prices for today / the last 7 days, the SGE daily closes otherwise
    // (the last close replaced by the live CMB price on a trading day).
    private (List<(DateTime Time, double Price)> Points, bool Daily, string Note) GoldChartPoints()
    {
        if (goldChartRange == "D")
            return (goldToday.ToList(), false, "今天的招行实时报价（鱼鱼每次取到的价格变化都记在 data\\gold-records.csv 里）。");
        if (goldChartRange == "W")
        {
            // Re-read when the variety or the day changed since the last read.
            string weekKey = $"{preferences.GoldVariety}|{DateTime.Today:yyyy-MM-dd}";
            if (goldWeekKey != weekKey) { goldWeekKey = weekKey; goldWeekRecords = GoldRecordsSince(DateTime.Today.AddDays(-6)); }
            var week = goldWeekRecords.Where(p => p.Time.Date < DateTime.Today).Concat(goldToday.Select(p => (Time: p.Local, p.Price))).OrderBy(p => p.Time).ToList();
            return (week, false, "近 7 天鱼鱼记下的招行实时报价（只有她开着的时候才有记录，所以中间可能有空白）。");
        }
        int days = goldChartRange switch { "1M" => 21, "3M" => 63, "1Y" => CompanionGoldForecast.YearDays, "3Y" => 3 * CompanionGoldForecast.YearDays, _ => int.MaxValue };
        var bars = goldBars.Skip(days == int.MaxValue ? 0 : Math.Max(0, goldBars.Count - days)).ToList();
        var points = bars.Select(b => (b.Date, b.Close)).ToList();
        if (points.Count > 0 && goldQuote is { } q && q.Code == preferences.GoldVariety && points[^1].Date == DateTime.Today)
            points[^1] = (points[^1].Date, q.Price);
        string note = "日线：上海黄金交易所同一品种的每日收盘价（东方财富）；浅蓝线是 20 日均线，浅蓝色扇形是未来的 68% 推测区间，虚线是你的计划价位。"
            + (preferences.GoldVariety == "AU100G" ? " Au100g 用 Au99.99 的日线。" : "");
        return (points, true, note);
    }

    // 20-day average aligned with the last `count` daily points.
    private List<double> GoldMovingAverage(int count)
    {
        var result = new List<double>();
        double sum = 0;
        for (int i = 0; i < goldBars.Count; i++)
        {
            sum += goldBars[i].Close;
            if (i >= 20) sum -= goldBars[i - 20].Close;
            if (i >= goldBars.Count - count) result.Add(i >= 19 ? sum / 20 : double.NaN);
        }
        return result;
    }

    private FrameworkElement BuildGoldTrend(List<(DateTime Time, double Price)> points, bool daily, GoldForecast? forecast, double planBuy, double planSell,
        List<double>? average)
    {
        const double width = 820, height = 300, left = 66, right = 18, top = 26, bottom = 30;
        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.White, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        TextBlock Label(string text, double x, double y, Brush brush, double size = 11)
        {
            var label = new TextBlock { Text = text, Foreground = brush, FontSize = size };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            canvas.Children.Add(label);
            return label;
        }
        if (points.Count < 2)
        {
            Label(goldChartRange is "D" or "W" ? "记录还不够画图：开着金价监测，过一会儿就有了。" : "等历史日线下载好再画图。", left, height / 2 - 10, GoldMuted, 13);
            return canvas;
        }

        int extra = 0;
        var fanRows = new List<GoldForecastRow>();
        if (daily && forecast != null && points.Count >= 15)
        {
            int room = Math.Max(3, Math.Min(21, points.Count / 4));
            fanRows = forecast.Rows.Where(r => r.Days <= room).ToList();
            extra = fanRows.Count > 0 ? fanRows[^1].Days : 0;
        }
        double slots = points.Count - 1 + extra;
        double spanSeconds = Math.Max(1, (points[^1].Time - points[0].Time).TotalSeconds);
        double X(int i) => left + (width - left - right) * (daily ? i / slots : (points[i].Time - points[0].Time).TotalSeconds / spanSeconds);
        double XSlot(double slot) => left + (width - left - right) * slot / slots;

        double min = points.Min(p => p.Price), max = points.Max(p => p.Price);
        foreach (var r in fanRows) { min = Math.Min(min, r.Low68); max = Math.Max(max, r.High68); }
        double range = Math.Max(max - min, max * 0.002);
        bool showBuy = planBuy > 0 && planBuy > min - range * .25 && planBuy < max + range * .25;
        bool showSell = planSell > 0 && planSell > min - range * .25 && planSell < max + range * .25;
        if (showBuy) { min = Math.Min(min, planBuy); max = Math.Max(max, planBuy); }
        if (showSell) { min = Math.Min(min, planSell); max = Math.Max(max, planSell); }
        range = Math.Max(max - min, max * 0.002);
        min -= range * .06;
        max += range * .06;
        double Y(double price) => top + (height - top - bottom) * (max - price) / (max - min);

        for (int g = 0; g <= 4; g++)
        {
            double value = min + (max - min) * g / 4, y = Y(value);
            canvas.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y, Stroke = GoldGridLine, StrokeThickness = 1 });
            Label(CompanionGoldForecast.Price(value), 6, y - 8, GoldMuted);
        }
        string Stamp(DateTime t) => daily ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : goldChartRange == "D" ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("M-d HH:mm", CultureInfo.InvariantCulture);
        Label(Stamp(points[0].Time), left, height - bottom + 8, GoldMuted);
        Label(Stamp(points[points.Count / 2].Time), X(points.Count / 2) - 30, height - bottom + 8, GoldMuted);
        Label(Stamp(points[^1].Time), X(points.Count - 1) - (extra > 0 ? 30 : 70), height - bottom + 8, GoldMuted);

        if (fanRows.Count > 0)
        {
            var fan = new Polygon { Fill = GoldFan };
            double baseSlot = points.Count - 1, basePrice = forecast!.Base;
            fan.Points.Add(new Point(XSlot(baseSlot), Y(basePrice)));
            foreach (var r in fanRows) fan.Points.Add(new Point(XSlot(baseSlot + r.Days), Y(r.High68)));
            foreach (var r in Enumerable.Reverse(fanRows)) fan.Points.Add(new Point(XSlot(baseSlot + r.Days), Y(r.Low68)));
            canvas.Children.Add(fan);
            var lastRow = fanRows[^1];
            Label($"{lastRow.Label}：{CompanionGoldForecast.Price(lastRow.Low68)}～{CompanionGoldForecast.Price(lastRow.High68)}",
                Math.Max(left, XSlot(baseSlot + lastRow.Days) - 150), Math.Max(top, Y(lastRow.High68) - 18), GoldAccent);
        }
        if (average != null && average.Count == points.Count)
        {
            var ma = new Polyline { Stroke = GoldMa, StrokeThickness = 1.2 };
            for (int i = 0; i < average.Count; i++) if (double.IsFinite(average[i])) ma.Points.Add(new Point(X(i), Y(average[i])));
            if (ma.Points.Count > 1) canvas.Children.Add(ma);
        }
        void PlanLine(double price, Brush brush, string name)
        {
            double y = Y(price);
            canvas.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 5, 4 } });
            Label($"{name} {CompanionGoldForecast.Price(price)}", left + 4, y - 16, brush);
        }
        if (showBuy) PlanLine(planBuy, GoldPlanBuy, "计划买入");
        if (showSell) PlanLine(planSell, GoldPlanSell, "计划卖出");

        var line = new Polyline { Stroke = points[^1].Price >= points[0].Price ? GoldUp : GoldDown, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < points.Count; i++) line.Points.Add(new Point(X(i), Y(points[i].Price)));
        canvas.Children.Add(line);
        var lastDot = new Ellipse { Width = 7, Height = 7, Fill = line.Stroke };
        Canvas.SetLeft(lastDot, X(points.Count - 1) - 3.5);
        Canvas.SetTop(lastDot, Y(points[^1].Price) - 3.5);
        canvas.Children.Add(lastDot);
        double change = (points[^1].Price / points[0].Price - 1) * 100;
        Label($"{Stamp(points[0].Time)} 到现在 {change.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}%　最高 {CompanionGoldForecast.Price(points.Max(p => p.Price))}　最低 {CompanionGoldForecast.Price(points.Min(p => p.Price))}",
            left, 4, GoldInk, 12);

        // Hover: the nearest point's time and price.
        var cross = new Line { Stroke = GoldMuted, StrokeThickness = 1, Y1 = top, Y2 = height - bottom, Visibility = Visibility.Hidden, IsHitTestVisible = false };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = GoldAccent, Visibility = Visibility.Hidden, IsHitTestVisible = false };
        var tipText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        var tip = new Border { Background = GoldAccent, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Child = tipText,
            Visibility = Visibility.Hidden, IsHitTestVisible = false };
        var hit = new Rectangle { Width = width - left - right, Height = height - top - bottom, Fill = Brushes.Transparent };
        Canvas.SetLeft(hit, left);
        Canvas.SetTop(hit, top);
        canvas.Children.Add(hit);
        canvas.Children.Add(cross);
        canvas.Children.Add(dot);
        canvas.Children.Add(tip);
        hit.MouseMove += (_, e) =>
        {
            double x = e.GetPosition(canvas).X;
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                double distance = Math.Abs(X(i) - x);
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            double px = X(best), py = Y(points[best].Price);
            cross.X1 = cross.X2 = px;
            Canvas.SetLeft(dot, px - 4);
            Canvas.SetTop(dot, py - 4);
            tipText.Text = $"{(daily ? points[best].Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : points[best].Time.ToString("M-d HH:mm:ss", CultureInfo.InvariantCulture))}　{points[best].Price:0.00}"
                + (average != null && best < average.Count && double.IsFinite(average[best]) ? $"　20 日均线 {average[best]:0.00}" : "");
            Canvas.SetLeft(tip, Math.Min(px + 8, width - 250));
            Canvas.SetTop(tip, Math.Max(top, py - 30));
            cross.Visibility = dot.Visibility = tip.Visibility = Visibility.Visible;
        };
        hit.MouseLeave += (_, _) => cross.Visibility = dot.Visibility = tip.Visibility = Visibility.Hidden;
        return canvas;
    }
}
