using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

internal sealed class DeltaForceRaid
{
    public DateTime Utc { get; set; }
    public string Map { get; set; } = "";
    public bool Extracted { get; set; }
    public long Haul { get; set; }
    public double Minutes { get; set; }
    public bool Manual { get; set; }
}

// Delta Force「烽火地带」record, stored only in data/deltaforce.json.
internal sealed class DeltaForceStats
{
    public string Day { get; set; } = "";
    public int TodayExtracts { get; set; }
    public int TodayDeaths { get; set; }
    public long TodayHaul { get; set; }
    public int TotalExtracts { get; set; }
    public int TotalDeaths { get; set; }
    public long TotalHaul { get; set; }
    public int Streak { get; set; } // > 0: extractions in a row, < 0: deaths in a row
    public int BestStreak { get; set; }
    public List<DeltaForceRaid> Recent { get; set; } = new();

    internal const string FileName = "deltaforce.json";
    internal int TodayRaids => TodayExtracts + TodayDeaths;
    internal int TotalRaids => TotalExtracts + TotalDeaths;

    // A gaming day lasts until 5 a.m., so a late-night session stays one day.
    internal static string DayOf(DateTime local) => local.AddHours(-5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DeltaForceStats Load()
    {
        try
        {
            if (File.Exists(FileName) && JsonSerializer.Deserialize<DeltaForceStats>(File.ReadAllText(FileName)) is { } stats)
            {
                stats.Recent ??= new();
                stats.Recent.RemoveAll(r => r == null);
                stats.Day ??= "";
                return stats;
            }
        }
        catch { }
        return new DeltaForceStats();
    }

    internal void Save()
    {
        try
        {
            File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FileName + ".tmp", FileName, true);
        }
        catch { }
    }

    internal void RollDay(DateTime local)
    {
        var day = DayOf(local);
        if (Day == day) return;
        Day = day;
        TodayExtracts = TodayDeaths = 0;
        TodayHaul = 0;
    }

    internal DeltaForceRaid Record(bool extracted, string map, double minutes, bool manual, DateTime local)
    {
        RollDay(local);
        if (extracted)
        {
            TodayExtracts++;
            TotalExtracts++;
            Streak = Streak > 0 ? Streak + 1 : 1;
            BestStreak = Math.Max(BestStreak, Streak);
        }
        else
        {
            TodayDeaths++;
            TotalDeaths++;
            Streak = Streak < 0 ? Streak - 1 : -1;
        }
        var raid = new DeltaForceRaid { Utc = DateTime.UtcNow, Map = map, Extracted = extracted, Minutes = Math.Round(minutes, 1), Manual = manual };
        Recent.Add(raid);
        if (Recent.Count > 40) Recent.RemoveRange(0, Recent.Count - 40);
        return raid;
    }

    internal void SetHaul(DeltaForceRaid raid, long haul, DateTime local)
    {
        RollDay(local);
        long change = haul - raid.Haul;
        raid.Haul = haul;
        if (DayOf(raid.Utc.ToLocalTime()) == Day) TodayHaul = Math.Max(0, TodayHaul + change);
        TotalHaul = Math.Max(0, TotalHaul + change);
    }

    // The owner corrects a result she misread from the screen.
    internal void Flip(DeltaForceRaid raid, DateTime local)
    {
        RollDay(local);
        bool today = DayOf(raid.Utc.ToLocalTime()) == Day;
        if (raid.Extracted)
        {
            if (raid.Haul > 0) SetHaul(raid, 0, local);
            TotalExtracts--; TotalDeaths++;
            if (today) { TodayExtracts--; TodayDeaths++; }
        }
        else
        {
            TotalDeaths--; TotalExtracts++;
            if (today) { TodayDeaths--; TodayExtracts++; }
        }
        TotalExtracts = Math.Max(0, TotalExtracts); TotalDeaths = Math.Max(0, TotalDeaths);
        TodayExtracts = Math.Max(0, TodayExtracts); TodayDeaths = Math.Max(0, TodayDeaths);
        raid.Extracted = !raid.Extracted;
        raid.Manual = true;
        int streak = 0;
        for (int i = Recent.Count - 1; i >= 0; i--)
        {
            bool extracted = Recent[i].Extracted;
            if (streak == 0) streak = extracted ? 1 : -1;
            else if (streak > 0 == extracted) streak += extracted ? 1 : -1;
            else break;
        }
        Streak = streak;
        BestStreak = Math.Max(BestStreak, Streak);
    }

    internal void Reset()
    {
        TodayExtracts = TodayDeaths = TotalExtracts = TotalDeaths = Streak = BestStreak = 0;
        TodayHaul = TotalHaul = 0;
        Recent.Clear();
    }

    internal static string Money(long value) =>
        value >= 100_000_000 ? $"{value / 1e8:0.##} 亿" : value >= 10_000 ? $"{value / 1e4:0.#} 万" : value.ToString(CultureInfo.InvariantCulture);

    internal string TodayLine() => TodayRaids == 0 ? "今天还没有打烽火"
        : $"今天 {TodayRaids} 把：撤离 {TodayExtracts}、阵亡 {TodayDeaths}（撤离率 {TodayExtracts * 100 / TodayRaids}%）"
          + (TodayHaul > 0 ? $"，带出约 {Money(TodayHaul)} 哈夫币" : "");

    internal string StreakLine() => Streak >= 2 ? $"已经连续撤离 {Streak} 把" : Streak <= -2 ? $"已经连续阵亡 {-Streak} 把" : "";

    internal IEnumerable<DeltaForceRaid> Today() => Recent.Where(r => DayOf(r.Utc.ToLocalTime()) == Day);

    internal static string Describe(DeltaForceRaid raid) =>
        $"{raid.Utc.ToLocalTime():HH:mm} {(raid.Map.Length > 0 ? raid.Map : "烽火")} {(raid.Extracted ? "撤离" : "阵亡")}"
        + (raid.Minutes > 0 ? $" {raid.Minutes:0} 分钟" : "") + (raid.Haul > 0 ? $" 带出 {Money(raid.Haul)}" : "");

    internal string Advice(DateTime local)
    {
        var tips = new List<string>();
        int rate = TodayRaids == 0 ? 0 : TodayExtracts * 100 / TodayRaids;
        if (Streak <= -3) tips.Add("连着寄了几把，先跑两把刀或者换张图找找手感，千万别上头");
        else if (TodayRaids >= 4 && rate < 35) tips.Add("撤离率有点低，最近带便宜点的装备，背包满了就撤、见好就收");
        else if (TodayRaids >= 4 && rate >= 70) tips.Add("撤离率超高！状态这么好，可以带好一点的装备去冲大红啦");
        else if (Streak >= 3) tips.Add("手感正热，但别贪，见好就收～");
        var early = Today().Count(r => !r.Extracted && r.Minutes is > 0 and < 3);
        if (early >= 2) tips.Add($"今天有 {early} 把是刚落地就寄了，开局先找掩体、听听脚步再动");
        if (local.Hour >= 23 || local.Hour < 5) tips.Add("已经很晚了，打完这把就去睡觉吧");
        else if (TodayRaids >= 12) tips.Add("今天打了好多把，起来活动活动、让眼睛休息一下");
        return tips.Count == 0 ? "稳稳地玩，鱼鱼一直给你望风～" : string.Join("；", tips) + "。";
    }

    internal string Review(DateTime local)
    {
        RollDay(local);
        if (TodayRaids == 0) return "今天还没有烽火战绩哦～进图之后鱼鱼会帮你记着，撤离、阵亡都算！";
        var text = new StringBuilder(TodayLine()).Append('。');
        if (StreakLine() is { Length: > 0 } streak) text.Append(streak).Append('。');
        var favourite = Today().Where(r => r.Map.Length > 0).GroupBy(r => r.Map).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (favourite != null && favourite.Count() >= 2)
            text.Append($"最常去{favourite.Key}（{favourite.Count()} 把，撤出 {favourite.Count(r => r.Extracted)} 把）。");
        var best = Today().Where(r => r.Haul > 0).OrderByDescending(r => r.Haul).FirstOrDefault();
        if (best != null) text.Append($"最肥的一把带出 {Money(best.Haul)} 哈夫币。");
        return text.Append(Advice(local)).ToString();
    }
}

// Delta Force「烽火地带」(extraction mode): recognises deployment, extraction, death and being downed from
// the game screen, keeps a record, reacts with fitting animations and lines, and lets chat talk shop.
public partial class MainWindow
{
    private DeltaForceStats? deltaStats;
    private DeltaForceStats DeltaStats => deltaStats ??= DeltaForceStats.Load();
    private bool gameDeltaForce;   // the current game session is Delta Force
    private bool deltaForeground;  // Delta Force is the foreground window right now
    private DateTime deltaLastSeenUtc = DateTime.MinValue, gameEndUtc = DateTime.MinValue;
    private string gameEndName = "";
    private DateTime nextDeltaRead, deltaRaidStartUtc = DateTime.MinValue, deltaLastResultUtc = DateTime.MinValue;
    private DateTime deltaDownedCooldown = DateTime.MinValue, deltaRoundCooldown = DateTime.MinValue, deltaSummaryDue = DateTime.MaxValue;
    private string deltaRaidMap = "";
    private DeltaForceRaid? deltaLastRaid;
    private long deltaHaulCandidate;
    private bool deltaReading, deltaHaulSpoken;
    private bool deltaResultScreenUp; // a result banner is (or, as far as she knows, still is) on screen
    private int deltaClearReadings;   // readings in a row without a result banner
    private DateTime deltaHavocUtc = DateTime.MinValue; // last Havoc Warfare (全面战场) frame: its deaths are not raids
    private int deltaSessionRaids;
    private TimeSpan deltaSessionPlayed;
    private string deltaLastLogged = "", deltaStatus = "还没有识别过游戏画面。";
    private string? deltaChatNote;

    private const string DeltaGlossary = "【三角洲行动·烽火地带常识】搜打撤玩法：带装备进图（零号大坝、长弓溪谷、巴克什、航天基地、潮汐监狱、核电站），" +
        "搜刮物资后到撤离点撤离才算成功，阵亡会丢掉带进去的装备。难度有常规、机密、绝密。黑话：摸金=搜物资赚钱；跑刀=只带刀或便宜装备进图白嫖；" +
        "大红=最值钱的红色品质物资；哈夫币=游戏货币；鼠鼠/屯屯鼠=苟着搜东西的玩家；老六=躲起来阴人的玩家；舔包=搜别人掉的包；" +
        "落地成盒=刚进图就阵亡；欧皇/非酋=运气好/差；寄了=阵亡。";
    // Words that only make sense for Delta Force; everyday words (撤离, 复盘, 寄了) count only while she knows the owner is playing.
    private static readonly Regex DeltaTopic = new("三角洲|烽火|跑刀|哈夫币|落地成盒|舔包|零号大坝|长弓溪谷|巴克什|航天基地|潮汐监狱|撤离点");
    private static readonly Regex DeltaReviewAsk = new(
        @"^(鱼鱼)?[，,]?(帮我|给我|来|陪我)?(复盘|看看战绩|说说战绩|总结一下战绩)(一下)?[吧呗呀嘛]?[。！!～~]*$" +
        "|(今天|今晚|刚才|这几把|最近)的?(烽火|三角洲)?战绩|(烽火|三角洲).{0,6}(战绩|复盘)|(今天|今晚).{0,4}(打了|撤了|寄了)几把");
    private static readonly Regex DeltaQuestion = new("[吗嘛呢？?]|是不是|有没有|怎么|要不要");
    // Negations and near-misses (没/不是/差点/队友…) in front of a result word cancel it.
    private const string DeltaNot = "(?<!没)(?<!没有)(?<!没能)(?<!不是)(?<!差点)(?<!差点就)(?<!差一点)(?<!险些)(?<!队友)";
    private static readonly Regex DeltaExtractSaid = new(
        DeltaNot + "撤离成功|" + DeltaNot + "成功撤离|" + DeltaNot + "撤出(来|去)了|^(看错了?|不对|其实|错了)?[，,]?(我|这把|刚才)?撤了[!！~～。]*$");
    private static readonly Regex DeltaDeathSaid = new(
        "撤离失败|(?<!差点)(?<!差一点)(?<!险些)没(能|有)?(成功)?撤(出来|出去|离)|" + DeltaNot + "阵亡|落地成盒|" + DeltaNot + "被(人|老六)?(打死|击杀)|" +
        "^(看错了?|不对|其实|错了)?[，,]?(我|又|这把|刚才|直接)*(寄|死)了[!！~～。…]*$");
    private static readonly Regex DeltaRedSaid = new("(?<!没)(?<!没有)(?<!没能)(出|摸到|搜到|捡到|开出|舔到)了?(个|一个|两个|三个)?大红");

    private static readonly string[] DeltaGreetLines =
    [
        "三角洲启动！{0}今天是摸金还是跑刀？鱼鱼给你望风～",
        "又要去烽火地带啦？{0}记得带够药和甲，鱼鱼在旁边盯着！",
        "鼠鼠出动！{0}今天也要满载而归哦～"
    ];
    private static readonly string[] DeltaCheers =
    [
        "{0}耳朵竖起来，听脚步～", "背包满了就撤，别贪！见好就收～", "这把有大红吗？鱼鱼眼睛都看直了～",
        "鼠鼠也有春天，苟到撤离点就是胜利！", "记得打药、修甲哦～", "舔包别舔太久，小心老六！", "{0}冲呀！鱼鱼给你望风～"
    ];
    private static readonly string[] DeltaRaidLines =
    [
        "进图啦！{1}～{0}这把是摸金还是跑刀？鱼鱼帮你望风！",
        "{1}到了！耳朵竖起来听脚步，鱼鱼也帮你盯着～",
        "出发{1}！大红在等{0}，但活着撤出来最重要哦～",
        "{1}！老六可能就躲在拐角，{0}小心点～",
        "鼠鼠潜入{1}……嘘——鱼鱼也不出声了"
    ];
    private static readonly string[] DeltaExtractLines =
    [
        "撤离成功！！{0}把哈夫币背回家啦～鱼鱼给你放烟花！",
        "安全撤离～仓库又胖了一圈，嘿嘿！",
        "活着出来就是赢！{0}好稳！",
        "撤出来啦！鱼鱼刚才紧张得尾巴都僵了……",
        "{0}撤离成功～这把摸到大红了吗？给鱼鱼看看！"
    ];
    private static readonly string[] DeltaQuickExtractLines =
    [
        "这么快就撤了？{0}这是跑刀白嫖吧，聪明！", "进去转一圈就出来～稳稳的幸福！"
    ];
    private static readonly string[] DeltaStreakLines =
    [
        "{1} 连撤！{0}今天是欧皇附体吧！", "又撤出来了！{1} 连撤，鼠鼠之王就是{0}！", "{1} 把全撤！鱼鱼要给{0}跳支舞～"
    ];
    private static readonly string[] DeltaComebackLines =
    [
        "终于撤出来了！！呜呜鱼鱼要哭了，{0}最棒！", "连寄之后的撤离最香了！{0}翻盘啦～"
    ];
    private static readonly string[] DeltaDeathLines =
    [
        "啊……寄了。装备没了还能再买，{0}没事就好～",
        "可恶！是哪个老六阴的{0}！鱼鱼记住他了！",
        "这把亏了点哈夫币……没关系，下把一定翻盘！",
        "呜，包没了……鱼鱼的小鱼干分{0}一半，别难过～",
        "被打了个措手不及……{0}深呼吸，下一把稳稳的～"
    ];
    private static readonly string[] DeltaEarlyDeathLines =
    [
        "落地成盒……包都还没摸热呢 (；´д｀)",
        "诶？这么快……{0}刚刚是去送快递了吗？下把慢一点嘛～",
        "开局就寄……这谁顶得住啊！鱼鱼帮{0}骂两句：坏人！"
    ];
    private static readonly string[] DeltaTiltLines =
    [
        "又寄了！？连跪 {1} 把了……谁在欺负{0}！鱼鱼生气了！",
        "{1} 把没撤出来了……要不先跑两把刀，攒攒手感？",
        "连寄 {1} 把……{0}别上头，换张图或者跑刀缓一缓吧～"
    ];
    private static readonly string[] DeltaTiltBreakLines =
    [
        "已经连寄 {1} 把了……{0}先停一下，喝口水、活动活动，再回来翻盘好不好？",
        "{1} 连跪……心态比装备重要！鱼鱼陪{0}休息十分钟吧～"
    ];
    private static readonly string[] DeltaDownedLines =
    [
        "倒了倒了！快爬到掩体后面！", "{0}被击倒了！队友快来救呀！", "撑住！鱼鱼给你加油，快自救！"
    ];
    private static readonly string[] DeltaBigHaulLines =
    [
        "这把带出 {1} 哈夫币！？{0}是欧皇吧！鱼鱼也要摸金！", "{1} 哈夫币！！仓库要装不下啦～{0}今晚加鸡腿！"
    ];
    private static readonly string[] DeltaGoodHaulLines = ["带出 {1} 哈夫币～仓库鼓起来啦！", "这把赚了 {1}，稳稳的！"];
    private static readonly string[] DeltaSmallHaulLines = ["带出 {1}，跑刀也是赚，稳稳的幸福～", "{1} 也是钱！苍蝇腿也是肉嘛～"];
    private static readonly string[] DeltaExtractAnimations = ["放烟花", "余额-金袋叮当", "工作状态-雀跃庆祝", "余额-钱袋如常"];
    private static readonly string[] DeltaDeathAnimations = ["余额-袋空如洗", "点击回应-害羞惊讶", "余额-数金皱眉"];

    private bool DeltaRaidOpen(DateTime now) => deltaRaidStartUtc > deltaLastResultUtc && (now - deltaRaidStartUtc).TotalMinutes < 60;
    private bool DeltaRecent(DateTime now) => gameDeltaForce || (now - deltaLastSeenUtc).TotalHours < 3;

    private bool IsDeltaForceWindow(IntPtr hwnd, uint pid)
    {
        var (path, name) = ProcessIdentity(pid);
        string lowerName = name.ToLowerInvariant(), lowerPath = path.ToLowerInvariant();
        if (lowerName.Contains("launcher")) return false;
        if (lowerName.StartsWith("deltaforce", StringComparison.Ordinal) || lowerPath.Contains(@"\delta force\") || lowerPath.Contains("三角洲")) return true;
        var title = new StringBuilder(256);
        CompanionNative.GetWindowText(hwnd, title, title.Capacity);
        string text = title.ToString();
        return text.Contains("三角洲行动") || text.Contains("Delta Force", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DeltaForce", StringComparison.OrdinalIgnoreCase);
    }

    private string DeltaGreeting()
    {
        var stats = DeltaStats;
        stats.RollDay(DateTime.Now);
        if (stats.TodayRaids > 0)
            return $"{bond.Address}又来烽火啦？今天已经撤了 {stats.TodayExtracts} 把、寄了 {stats.TodayDeaths} 把，这把稳稳的！";
        return Fill(Pick(DeltaGreetLines));
    }

    private void DeltaSay(string text, string? animation, bool important = false)
    {
        if (!preferences.GameCompanion || !IsVisible || PuppetActive || chatWindow != null || companionClosed) return;
        Say(text, animation, important);
    }

    private void PlayDeltaAnimation(string animation)
    {
        if (dragging || PuppetActive || !IsVisible || TryFindRule(animation) is not { } rule) return;
        CancelMovement();
        PlayAnimation(rule, "Interact");
    }

    // A short break (alt-tab, a meal) between raids keeps the same session and its pending review.
    private void StartDeltaSession(bool deltaForce)
    {
        if (!deltaForce) return;
        if (deltaSummaryDue != DateTime.MaxValue) { deltaSummaryDue = DateTime.MaxValue; return; }
        deltaSessionRaids = 0;
        deltaSessionPlayed = TimeSpan.Zero;
        deltaResultScreenUp = false;
        deltaClearReadings = 0;
    }

#if COMPANION_OCR
    private async Task ReadDeltaForceScreen(IntPtr hwnd)
    {
        deltaReading = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var snapshot = await Task.Run(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var image = CompanionScreenCapture.CaptureGame(hwnd, 1600) ?? throw new IOException("没有截到游戏画面");
                return await CompanionOcr.RecognizeAsync(image, "", watch.Elapsed.TotalMilliseconds, limit.Token).ConfigureAwait(false);
            }, limit.Token);
            if (!gameActive || companionClosed) return;
            var reading = DeltaForceScreen.Analyze(snapshot.Words, snapshot.Width, snapshot.Height);
            deltaStatus = $"{DateTime.Now:HH:mm:ss} 读到 {snapshot.WordCount} 个文字块（{snapshot.Language}，{snapshot.Width}×{snapshot.Height}）"
                + (reading.Event == DeltaForceEvent.None ? "" : $"，识别到「{DeltaEventName(reading.Event)}」")
                + (snapshot.WordCount == 0 ? "。一直读不到文字时，请把游戏显示模式改为无边框窗口。" : "。");
            OnDeltaForceReading(reading, DateTime.UtcNow);
        }
        catch (Exception ex) { deltaStatus = $"{DateTime.Now:HH:mm:ss} 识别失败：{ex.Message}"; }
        finally { deltaReading = false; }
    }
#endif

    private static string DeltaEventName(DeltaForceEvent e) => e switch
    {
        DeltaForceEvent.RaidStart => "进图",
        DeltaForceEvent.Extracted => "撤离成功",
        DeltaForceEvent.Died => "阵亡",
        DeltaForceEvent.Downed => "被击倒",
        DeltaForceEvent.Victory => "胜利",
        DeltaForceEvent.Defeat => "失败",
        _ => "无"
    };

    private void OnDeltaForceReading(DeltaForceReading reading, DateTime now)
    {
        LogDeltaForce(reading);
        if (reading.Havoc) deltaHavocUtc = now;
        bool result = reading.Event is DeltaForceEvent.Extracted or DeltaForceEvent.Died;
        bool screenWasUp = deltaResultScreenUp;
        if (result) { deltaResultScreenUp = true; deltaClearReadings = 0; }
        else if (deltaResultScreenUp && ++deltaClearReadings >= 2) deltaResultScreenUp = false;
        switch (reading.Event)
        {
            case DeltaForceEvent.Extracted:
            case DeltaForceEvent.Died:
                // A result counts once: not while the same screen stays up (even across an alt-tab), and not again
                // within a few minutes (death banner, then the settlement) unless a new deployment was seen.
                bool sameKind = deltaLastRaid != null && deltaLastRaid.Extracted == (reading.Event == DeltaForceEvent.Extracted);
                bool fresh = deltaRaidStartUtc > deltaLastResultUtc || (now - deltaLastResultUtc).TotalMinutes >= (sameKind ? 10 : 6);
                bool havoc = (now - deltaHavocUtc).TotalMinutes < 10 && deltaHavocUtc > deltaRaidStartUtc;
                if (screenWasUp || !fresh || havoc) break;
                var (line, animation, important) = RecordDeltaRaid(reading.Event == DeltaForceEvent.Extracted, reading.Map, false, now);
                DeltaSay(line, animation, important);
                break;
            case DeltaForceEvent.RaidStart:
                DeltaRaidStart(reading.Map, now);
                break;
            case DeltaForceEvent.Downed:
                if (now >= deltaDownedCooldown && (now - deltaLastResultUtc).TotalSeconds > 60)
                {
                    deltaDownedCooldown = now.AddMinutes(2);
                    DeltaSay(Fill(Pick(DeltaDownedLines)), "点击回应-害羞惊讶");
                }
                break;
            case DeltaForceEvent.Victory:
            case DeltaForceEvent.Defeat:
                if (now >= deltaRoundCooldown)
                {
                    deltaRoundCooldown = now.AddMinutes(3);
                    bool won = reading.Event == DeltaForceEvent.Victory;
                    if (won) AddAffection(.3);
                    DeltaSay(Fill(Pick(won ? WinLines : LoseLines)), won ? "放烟花" : "余额-数金皱眉");
                }
                break;
        }
        if (reading.Haul > 0) AttachDeltaHaul(reading.Haul, now);
    }

    private void DeltaRaidStart(string map, DateTime now)
    {
        if (DeltaRaidOpen(now))
        {
            if (deltaRaidMap.Length == 0) deltaRaidMap = map;
            return;
        }
        if ((now - deltaLastResultUtc).TotalSeconds < 90) return; // still on the settlement screens
        deltaRaidStartUtc = now;
        deltaRaidMap = map;
        deltaResultScreenUp = false;
        DeltaSay(string.Format(Pick(DeltaRaidLines), bond.Address, map), companionRandom.NextDouble() < .5 ? "东张西望" : "工作状态-原地踱步张望");
    }

    // Records one raid and returns what she says about it (the caller decides whether to say it).
    private (string Line, string Animation, bool Important) RecordDeltaRaid(bool extracted, string map, bool manual, DateTime now)
    {
        bool open = DeltaRaidOpen(now);
        double minutes = open ? (now - deltaRaidStartUtc).TotalMinutes : 0;
        if (map.Length == 0 && open) map = deltaRaidMap;
        var stats = DeltaStats;
        int before = stats.Streak;
        var raid = stats.Record(extracted, map, minutes, manual, DateTime.Now);
        stats.Save();
        deltaLastRaid = raid;
        deltaHaulSpoken = false;
        deltaHaulCandidate = 0;
        deltaLastResultUtc = now;
        deltaSessionRaids++;
        if (extracted) { AddAffection(.3); SetMood("开心", TimeSpan.FromMinutes(20)); }
        else SetMood("心疼主人", TimeSpan.FromMinutes(15));
        RememberDeltaMoment(raid, before);
        return DeltaReaction(raid, before);
    }

    private (string Line, string Animation, bool Important) DeltaReaction(DeltaForceRaid raid, int streakBefore)
    {
        int streak = DeltaStats.Streak;
        if (raid.Extracted)
        {
            if (streak >= 3) return (string.Format(Pick(DeltaStreakLines), bond.Address, streak), companionRandom.NextDouble() < .5 ? "轻快摇摆舞" : "放烟花", false);
            if (streakBefore <= -3) return (Fill(Pick(DeltaComebackLines)), "工作状态-雀跃庆祝", false);
            if (raid.Minutes is > 0 and < 5) return (Fill(Pick(DeltaQuickExtractLines)), "余额-金袋叮当", false);
            return (Fill(Pick(DeltaExtractLines)), Pick(DeltaExtractAnimations), false);
        }
        if (raid.Minutes is > 0 and < 3) return (Fill(Pick(DeltaEarlyDeathLines)), "工作状态-垂头叹气冒汗", false);
        if (streak <= -5) return (string.Format(Pick(DeltaTiltBreakLines), bond.Address, -streak), "余额-分文不剩", true);
        if (streak <= -3) return (string.Format(Pick(DeltaTiltLines), bond.Address, -streak), "玩游戏气急败坏", false);
        return (Fill(Pick(DeltaDeathLines)), Pick(DeltaDeathAnimations), false);
    }

    // The settlement value often counts up on screen, so a number counts only once it reads the same twice.
    private void AttachDeltaHaul(long haul, DateTime now)
    {
        if (deltaLastRaid is not { Extracted: true } raid || (now - deltaLastResultUtc).TotalMinutes > 4) return;
        if (haul != deltaHaulCandidate) { deltaHaulCandidate = haul; return; }
        if (raid.Haul == haul) return;
        DeltaStats.SetHaul(raid, haul, DateTime.Now);
        DeltaStats.Save();
        if (deltaHaulSpoken) return;
        deltaHaulSpoken = true;
        string money = DeltaForceStats.Money(haul);
        if (haul >= 1_000_000)
        {
            RememberDelta($"主人在三角洲{(raid.Map.Length > 0 ? raid.Map : "烽火地带")}一把带出了约{money}哈夫币，超级欧皇", 2);
            DeltaSay(string.Format(Pick(DeltaBigHaulLines), bond.Address, money), "余额-钱袋满溢");
        }
        else if (haul >= 300_000) DeltaSay(string.Format(Pick(DeltaGoodHaulLines), bond.Address, money), "余额-金袋叮当");
        else if (haul < 100_000) DeltaSay(string.Format(Pick(DeltaSmallHaulLines), bond.Address, money), null);
    }

    private void RememberDelta(string text, int importance)
    {
        if (!preferences.MemoryEnabled || memory == null) return;
        memory.Remember(text, "", "event", importance);
        memory.Save();
    }

    private void RememberDeltaMoment(DeltaForceRaid raid, int streakBefore)
    {
        int streak = DeltaStats.Streak;
        string where = raid.Map.Length > 0 ? raid.Map : "烽火地带";
        if (raid.Extracted && streak == 5) RememberDelta($"主人在三角洲烽火地带连续撤离了 5 把，手感超好", 1);
        else if (raid.Extracted && streakBefore <= -4) RememberDelta($"主人在三角洲连寄 {-streakBefore} 把之后，终于在{where}撤离成功翻盘了", 1);
        else if (!raid.Extracted && streak == -5) RememberDelta("主人在三角洲烽火地带连续阵亡了 5 把，有点上头", 1);
        else if (!raid.Extracted && raid.Minutes is > 0 and < 3 && companionRandom.NextDouble() < .3)
            RememberDelta($"主人在三角洲{where}落地成盒了", 1);
    }

    // Delta Force has been out of sight for a while (UpdateGame): a short review instead of the generic goodbye.
    private void FinishDeltaSession()
    {
        deltaSummaryDue = DateTime.MaxValue;
        int raids = deltaSessionRaids;
        var played = deltaSessionPlayed;
        deltaSessionRaids = 0;
        deltaSessionPlayed = TimeSpan.Zero;
        if (raids == 0) { GameGoodbye("三角洲行动", played); return; }
        if (raids < 2 && played.TotalMinutes < 20) return;
        var stats = DeltaStats;
        var local = DateTime.Now;
        stats.RollDay(local);
        if (played.TotalMinutes >= 10) AddAffection(1);
        RememberDelta($"主人玩了三角洲烽火地带：{stats.TodayLine()}", 1);
        if (preferences.GameCompanion && CanChatter && !GameActive)
            Say("今天的烽火复盘：" + stats.Review(local), "工作状态-清点归档", raids >= 3);
    }

    // ---------------------------------------------------------------- chat
    // Handles what does not need the model: a review request, or the owner telling her a result.
    // Returns true when the message has been answered.
    private bool TryDeltaForceChat(string message)
    {
        deltaChatNote = null; // a note is only for the message that produced it
        var now = DateTime.UtcNow;
        if (!DeltaTopic.IsMatch(message) && !DeltaRecent(now)) return false;
        var stats = DeltaStats;
        var local = DateTime.Now;
        stats.RollDay(local);
        if (DeltaReviewAsk.IsMatch(message))
        {
            ChatReply(message, stats.Review(local), "工作状态-清点归档", remember: false);
            return true;
        }
        if (DeltaQuestion.IsMatch(message)) return false; // a question is not a report of a result
        if (DeltaRedSaid.IsMatch(message))
        {
            PlayDeltaAnimation("余额-钱袋满溢");
            RememberDelta("主人在三角洲摸到了大红", 2);
            deltaChatNote = "主人说自己刚摸到了大红！要替主人开心、夸主人是欧皇。";
            if (!ModelReadyForChat) { ChatReply(message, $"大红！！{bond.Address}是欧皇吧！一定要活着撤出来呀～", remember: false); deltaChatNote = null; return true; }
            return false;
        }
        // Results are reported in a few words; a long sentence without a Delta word is about something else.
        if (message.Length > 16 && !DeltaTopic.IsMatch(message)) return false;
        bool extracted = DeltaExtractSaid.IsMatch(message), died = DeltaDeathSaid.IsMatch(message);
        if (extracted == died) return false; // neither, or both (unclear)
        string note;
        string line, animation;
        double sinceResult = (now - deltaLastResultUtc).TotalMinutes;
        // Only a result read from the screen can be "already recorded" or corrected; what the owner reported is final.
        var last = deltaLastRaid is { Manual: false } screen && !DeltaRaidOpen(now) && sinceResult < 20 ? screen : null;
        bool another = message.Contains('又');
        if (last != null && last.Extracted == extracted && !another)
        {
            // Already recognised from the screen; the owner is just telling her about it.
            note = $"主人说这把{(extracted ? "撤离成功" : "阵亡")}了（鱼鱼刚才已经看到并记下了）。{stats.TodayLine()}。";
            (line, animation) = extracted ? (Fill(Pick(DeltaExtractLines)), Pick(DeltaExtractAnimations)) : (Fill(Pick(DeltaDeathLines)), Pick(DeltaDeathAnimations));
        }
        else if (last != null && last.Extracted != extracted && !another
            && (sinceResult < 2 || sinceResult < 10 && Regex.IsMatch(message, "看错|不对|错了|记错|其实")))
        {
            // She read the screen wrong a moment ago: the owner knows better.
            stats.Flip(last, local);
            stats.Save();
            note = $"鱼鱼刚才看错了，主人说这把其实{(extracted ? "撤离成功" : "阵亡")}了，已经改好战绩：{stats.TodayLine()}。";
            line = $"诶？鱼鱼看错啦……已经改成{(extracted ? "撤离成功" : "阵亡")}了！";
            animation = "点击回应-害羞惊讶";
        }
        else
        {
            (line, animation, _) = RecordDeltaRaid(extracted, "", true, now);
            note = $"主人告诉鱼鱼这把{(extracted ? "撤离成功" : "阵亡")}了，鱼鱼已经记进战绩：{stats.TodayLine()}。"
                + (stats.StreakLine() is { Length: > 0 } s ? s + "。" : "");
        }
        if (!ModelReadyForChat)
        {
            ChatReply(message, line + $"（{stats.TodayLine()}）", animation, remember: false);
            return true;
        }
        PlayDeltaAnimation(animation);
        deltaChatNote = note + (extracted ? "要替主人开心。" : "要安慰主人，可以吐槽一下打倒主人的老六。");
        return false;
    }

    private bool ModelReadyForChat => preferences.ModelEnabled && brain.ModelAvailable && brain.CanRunOnCurrentPower;

    private string DeltaForceChatContext(string message)
    {
        var now = DateTime.UtcNow;
        if (deltaChatNote == null && !DeltaTopic.IsMatch(message) && !DeltaRecent(now)) return "";
        var stats = DeltaStats;
        stats.RollDay(DateTime.Now);
        var text = new StringBuilder("\n").Append(DeltaGlossary);
        text.Append("\n【主人今天的烽火战绩】").Append(stats.TodayLine());
        if (stats.StreakLine() is { Length: > 0 } streak) text.Append("，").Append(streak);
        var recent = stats.Today().TakeLast(4).Select(DeltaForceStats.Describe).ToArray();
        if (recent.Length > 0) text.Append("。最近几把：").Append(string.Join("；", recent));
        text.Append('。');
        if (deltaChatNote != null) { text.Append("\n【刚刚】").Append(deltaChatNote); deltaChatNote = null; }
        text.Append("\n聊三角洲时像一起开黑的小伙伴一样，可以自然地用黑话吐槽或安慰，但不要编造主人没说过的战绩和数字。");
        return text.ToString();
    }

    // ---------------------------------------------------------------- log
    private void LogDeltaForce(DeltaForceReading reading)
    {
        if (!preferences.DeltaForceLog) return;
        if (reading.Event == DeltaForceEvent.None && (reading.BigText.Length == 0 || reading.BigText == deltaLastLogged)) return;
        deltaLastLogged = reading.BigText;
        try
        {
            const string file = "deltaforce-ocr.log";
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 256 * 1024) File.Move(file, "deltaforce-ocr.old.log", true);
            string big = reading.BigText.Length > 600 ? reading.BigText[..600] + "…" : reading.BigText;
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{DeltaEventName(reading.Event)}]"
                + (reading.Map.Length > 0 ? $" 地图={reading.Map}" : "") + (reading.Haul > 0 ? $" 价值={reading.Haul}" : "")
                + $" 文字块={reading.Words} 大字：{big}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    // ---------------------------------------------------------------- settings
    private string DeltaStatsText()
    {
        var stats = DeltaStats;
        stats.RollDay(DateTime.Now);
        var text = new StringBuilder(stats.TodayLine());
        if (stats.StreakLine() is { Length: > 0 } streak) text.Append(" · ").Append(streak);
        int rate = stats.TotalRaids == 0 ? 0 : stats.TotalExtracts * 100 / stats.TotalRaids;
        text.Append($"\n累计 {stats.TotalRaids} 把 · 撤离 {stats.TotalExtracts} · 阵亡 {stats.TotalDeaths} · 撤离率 {rate}% · 最长连撤 {stats.BestStreak}");
        if (stats.TotalHaul > 0) text.Append($" · 带出约 {DeltaForceStats.Money(stats.TotalHaul)} 哈夫币");
        var recent = stats.Recent.TakeLast(6).Reverse().ToArray();
        if (recent.Length > 0)
        {
            text.Append("\n最近：");
            foreach (var raid in recent) text.Append("\n  · ").Append(DeltaForceStats.Describe(raid)).Append(raid.Manual ? "（手动）" : "");
        }
        return text.ToString();
    }

    private void AddDeltaForceSettings(StackPanel page, Action<string> section, Action<string> note, Func<string, bool, Action<bool>, CheckBox> toggle)
    {
        section("三角洲行动 · 烽火地带");
#if COMPANION_OCR
        toggle("识别「烽火地带」的进图、撤离成功、被击倒和阵亡：记战绩，并配上专属动作和吐槽（本机截图识别文字，约每 6 秒一次）",
            preferences.DeltaForceReactions, v => preferences.DeltaForceReactions = v);
        toggle("保存识别日志 data\\deltaforce-ocr.log（只记录画面上的大字，识别不准时方便排查）", preferences.DeltaForceLog, v => preferences.DeltaForceLog = v);
        note("请把游戏的显示模式设为「无边框窗口 / 窗口化全屏」，独占全屏时可能截不到画面；需要 Windows 已安装中文文字识别。识别状态：" + deltaStatus);
#else
        note("这个版本没有本机文字识别，不能自动识别撤离和阵亡；可以在聊天里告诉鱼鱼「撤离成功了」「寄了」来记战绩。");
#endif
        var summary = new TextBlock { Text = DeltaStatsText(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        page.Children.Add(summary);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        void Add(Button button) { button.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(button); }
        // Adding a missed raid by hand must not close the raid that is running now.
        void AddRaid(bool extracted)
        {
            DeltaStats.Record(extracted, "", 0, true, DateTime.Now);
            DeltaStats.Save();
            summary.Text = DeltaStatsText();
        }
        Add(BigBlueFish.Deployment.ModernUI.Button("记一把撤离", () => AddRaid(true)));
        Add(BigBlueFish.Deployment.ModernUI.Button("记一把阵亡", () => AddRaid(false)));
        Add(BigBlueFish.Deployment.ModernUI.Button("清空战绩", () =>
        {
            if (BigBlueFish.Deployment.ModernUI.Choice(companionSettings, "清空烽火战绩", "今天和累计的烽火战绩都会清空。确定吗？", "清空", "再想想") != 0) return;
            DeltaStats.Reset();
            DeltaStats.Save();
            deltaLastRaid = null;
            summary.Text = DeltaStatsText();
        }));
        page.Children.Add(row);
        note("聊天里也可以说「撤离成功了」「寄了」「出大红了」，或者问鱼鱼「今天战绩怎么样」「帮我复盘」。");
    }
}
