using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

// A meme for her bubble: a picture from assets\dshpet\memes and what it shows (for the chat model).
internal sealed record CompanionMeme(string Name, string Description, string Path, bool DefaultOn);

// The parts taken over from @tuweizhi/dsh-pet (TuWeiZhi/dsh-pet, upstream PC2005-cloud/dsh-pet; code MIT): the local
// 碎碎念 line pool, the work-status texts, the throw physics, the catch score and the meme tag of the chat. Its pictures
// (memes, status icons, cursors, font) are not part of this repository: the deployment folder installs them into
// assets\dshpet for the owner's own use, and everything here works (without them) when they are missing.
internal static class CompanionDshPet
{
    internal static string Root => Path.Combine(AppContext.BaseDirectory, "assets", "dshpet");

    // dsh-pet's 40 built-in lines (WHISPER_LINES_BUILTIN), used while the owner's pool is empty.
    internal static readonly string[] BuiltinWhisperLines =
    [
        "我在这儿哦。", "主人，休息一下吧。", "今天也辛苦了。", "要不要摸摸我的头？", "我在旁边陪着你。",
        "喝口水吧。", "有点想吃零食了…", "我刚刚打了个盹。", "你忙你的，我不吵你。", "桌面上有我在，别怕。",
        "伸个懒腰吧。", "眼睛该休息一下了。", "我数着时间等你。", "今天过得怎么样？", "有你在真好。",
        "我偷偷给你加油。", "别看太久屏幕哦。", "我乖不乖？", "我有点困了…", "主人，我饿了。",
        "慢慢来，不着急。", "我就在右下角。", "肩膀放松一点。", "吃饭要按时哦。", "我一直都在。",
        "今天想被夸一句。", "我什么都没想。", "外面天气怎么样？", "记得喝水。", "我先眯一下下。",
        "你刚才叹气了？", "我在发呆，挺好。", "累了就靠一会儿。", "我替你看着时间。", "今天也很努力了。",
        "我想被抱一下。", "别熬太晚。", "我在这儿，随叫随到。", "有没有想说的话？", "我们慢慢来。"
    ];

    // The three 碎碎念 clips (animations.events.whisper).
    internal static readonly string[] WhisperClips = ["碎碎念-擦桌碎碎念", "碎碎念-发呆碎碎念", "碎碎念-对屏碎碎念"];

    // Work status: 0 thinking, 1 working, 2 one step done, 3 waiting for you, 4 done, 5 failed — clips and texts
    // (animations.events.workStatus and workStatusTexts).
    internal static readonly string[] WorkStatusClips =
        ["工作状态-思考冒泡", "工作状态-忙碌点按", "工作状态-清点归档", "工作状态-原地踱步张望", "工作状态-雀跃庆祝", "工作状态-垂头叹气冒汗"];
    internal static readonly string[][] WorkStatusTexts =
    [
        ["正在认真想下一步呢", "让我整理一下思路~", "正在梳理接下来要做什么"],
        ["正在处理任务呢", "这一步正在进行中哦", "正在继续干活呢，别打扰我~"],
        ["这一步处理好啦，接着看下一步", "搞定一步，继续看看哦~"],
        ["需要你确认一下呢", "这里要等你看一下哦", "轮到你来决定啦，等你~"],
        ["这一轮搞定啦，真棒！", "任务完成咯，可以摸鱼啦~"],
        ["这一步好像没跑通呢", "刚才的操作遇到点问题哦", "这里卡了一下，再等等你呢"]
    ];

    // ---------------------------------------------------------------- pictures
    internal static IReadOnlyList<CompanionMeme> LoadMemes(string root)
    {
        var memes = new List<CompanionMeme>();
        string folder = Path.Combine(root, "memes");
        if (!Directory.Exists(folder)) return memes;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "memes.json")));
            foreach (var item in document.RootElement.GetProperty("memes").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString() ?? "";
                string path = Path.Combine(folder, name + ".png");
                if (name.Length == 0 || Path.GetFileName(path) != name + ".png" || !File.Exists(path) || memes.Any(m => m.Name == name)) continue;
                string description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                bool on = !item.TryGetProperty("defaultOn", out var o) || o.ValueKind != JsonValueKind.False;
                memes.Add(new CompanionMeme(name, description, path, on));
            }
        }
        catch { } // no list or a broken one: the pictures still work, without descriptions
        try
        {
            foreach (var file in Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!memes.Any(m => m.Name == name)) memes.Add(new CompanionMeme(name, "", file, true));
            }
        }
        catch { }
        return memes;
    }

    // notify-done / -error / -question / -approval / -truncated / -test
    internal static string? IconPath(string name)
    {
        string path = Path.Combine(Root, "pic", "notify-" + name + ".png");
        return File.Exists(path) ? path : null;
    }

    // ---------------------------------------------------------------- 碎碎念
    // The owner's pool: one line per line, blank lines skipped, at most 200 characters each; empty → the built-in 40.
    internal static IReadOnlyList<string> WhisperPool(string? configured)
    {
        var lines = (configured ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.Length > 200 ? l[..200] : l).ToList();
        return lines.Count > 0 ? lines : BuiltinWhisperLines;
    }

    // A random line that is not the one she said last time (unless the pool has only one).
    internal static string PickLine(IReadOnlyList<string> pool, string? last, Random random)
    {
        if (pool.Count == 0) return "";
        if (pool.Count == 1) return pool[0];
        var options = pool.Where(l => l != last).ToList();
        return options.Count == 0 ? pool[random.Next(pool.Count)] : options[random.Next(options.Count)];
    }

    // ---------------------------------------------------------------- memes in the chat
    // 【图：名字】 at the end of a reply picks one meme; the tag itself never shows.
    private static readonly Regex MemeTag = new(@"[【\[]\s*(?:图|配图|表情|表情包)\s*[:：]\s*([^】\]\n]{1,24}?)\s*[】\]]");
    private static readonly Regex MemeTagUnfinished = new(@"[【\[]\s*(?:图|配图|表情|表情包)[^】\]\n]{0,26}$");

    internal static string? MemeName(string raw)
    {
        var matches = MemeTag.Matches(raw);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value.Trim();
    }

    internal static string StripMemeTags(string text)
    {
        string stripped = MemeTagUnfinished.Replace(MemeTag.Replace(text, ""), "");
        return stripped.Length == text.Length ? text : stripped.TrimEnd();
    }

    // The memes the chat model may pick from (the first `limit` of the enabled ones; 0 = all).
    internal static string MemeGuidance(IEnumerable<CompanionMeme> memes, int limit)
    {
        var offered = (limit > 0 ? memes.Take(limit) : memes).ToList();
        if (offered.Count == 0) return "";
        return "\n【表情包】回复可以配一张表情包：在回复最后单独写【图：名字】，最多一张，不合适就不配。名字只能从这些里选："
            + string.Join("；", offered.Select(m => m.Description.Length > 0 ? $"{m.Name}（{m.Description}）" : m.Name)) + "。";
    }

    // ---------------------------------------------------------------- throwing (src/shared/physics.ts)
    internal sealed record Physics(double Gravity, double Restitution, double Friction, bool Ceiling, double Power)
    {
        internal static readonly Physics Default = new(1400, .78, 2.5, true, 1);
    }

    internal record struct ThrowState(double X, double Y, double Vx, double Vy);

    internal const double MaxThrowSpeed = 3600, RestVy = 40, RestVx = 15, MaxStep = .05;

    // Release speeds are softly limited (they approach 3600 DIP/s but never pass it), then scaled by the power.
    internal static double SoftClamp(double speed) => MaxThrowSpeed * (1 - Math.Exp(-Math.Max(0, speed) / MaxThrowSpeed));

    // One step of the flight: gravity, walls with restitution, an optional ceiling, friction and resting on the floor.
    // Units: DIP and seconds; (minX, minY) … (maxX, maxY) are the allowed window positions.
    internal static (ThrowState State, bool Bounced, double Impact, bool Floor, bool AtRest) Step(ThrowState s, double dtRaw,
        double minX, double minY, double maxX, double maxY, Physics p)
    {
        double dt = Math.Clamp(dtRaw, 0, MaxStep);
        double x = s.X, y = s.Y, vx = s.Vx, vy = s.Vy;
        bool ceiling = p.Ceiling || p.Gravity <= 0; // without gravity she would never come back from above
        vy += p.Gravity * dt;
        if (p.Gravity <= 0) { double drag = Math.Exp(-dt / .7); vx *= drag; vy *= drag; } // weightless: drifts to a stop
        x += vx * dt;
        y += vy * dt;
        bool bounced = false, floor = false;
        double impact = 0;
        if (x < minX) { x = minX; impact = Math.Abs(vx); vx = Math.Abs(vx) * p.Restitution; bounced = true; }
        else if (x > maxX) { x = maxX; impact = Math.Abs(vx); vx = -Math.Abs(vx) * p.Restitution; bounced = true; }
        if (y < minY)
        {
            if (ceiling) { y = minY; impact = Math.Max(impact, Math.Abs(vy)); vy = Math.Abs(vy) * p.Restitution; bounced = true; }
        }
        else if (y >= maxY)
        {
            y = maxY;
            floor = true;
            vx *= Math.Max(0, 1 - p.Friction * dt);
            if (Math.Abs(vy) < RestVy) vy = 0;
            else { impact = Math.Max(impact, Math.Abs(vy)); vy = -Math.Abs(vy) * p.Restitution; bounced = true; }
        }
        double speed = Math.Sqrt(vx * vx + vy * vy);
        bool atRest = p.Gravity > 0
            ? y >= maxY - 1 && Math.Abs(vy) < 1 && Math.Abs(vx) < RestVx || bounced && speed < RestVy && Math.Abs(vy) < 1
            : speed < RestVx;
        return (new ThrowState(x, y, vx, vy), bounced, impact, floor, atRest);
    }

    // How much she squashes when she hits something: a soft landing a little, a hard one up to 0.22.
    internal static double ImpactSquash(double impact) => impact < 150 ? 0 : .05 + Math.Clamp((impact - 300) / 1200, 0, 1) * .17;

    // ---------------------------------------------------------------- catching her in flight (src/shared/score.ts)
    internal const double ScoreMinSpeed = 400, ScoreReferenceWidth = 462;

    // round(speed / 100 × 462 / width), at least 1: faster and smaller is worth more.
    internal static int CatchScore(double speed, double width) =>
        speed <= 0 || width <= 0 ? 0 : Math.Max(1, (int)Math.Round(speed / 100 * ScoreReferenceWidth / width));
}

// When it is and what day it is, for the chat (after dsh-pet's time-context.ts): part of the day, weekday or weekend,
// and festivals today or in the next three days (lunar ones through the Chinese calendar of .NET).
internal static class CompanionTimeContext
{
    private static readonly Dictionary<int, List<(DateTime Date, string Name)>> years = new();

    internal static string Period(DateTime t) => t.Hour switch
    {
        < 5 => "凌晨", < 8 => "早上", < 11 => "上午", < 13 => "中午", < 17 => "下午", < 19 => "傍晚", < 23 => "晚上", _ => "深夜"
    };

    internal static bool Weekend(DateTime t) => t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    internal static IReadOnlyList<(DateTime Date, string Name)> Festivals(int year)
    {
        lock (years)
        {
            if (years.TryGetValue(year, out var cached)) return cached;
            var list = new List<(DateTime, string)>();
            void Add(int month, int day, string name) { try { list.Add((new DateTime(year, month, day), name)); } catch (ArgumentOutOfRangeException) { } }
            Add(1, 1, "元旦"); Add(2, 14, "情人节"); Add(3, 8, "妇女节"); Add(5, 1, "劳动节"); Add(5, 20, "520");
            Add(6, 1, "儿童节"); Add(9, 10, "教师节"); Add(10, 1, "国庆节"); Add(10, 31, "万圣节"); Add(12, 24, "平安夜"); Add(12, 25, "圣诞节");
            // 清明 and 冬至 by the usual 21st-century formula (good from 2001 to 2099)
            int y = year % 100;
            if (year is > 2000 and < 2100)
            {
                Add(4, (int)Math.Floor(y * .2422 + 4.81) - (y - 1) / 4, "清明节");
                Add(12, (int)Math.Floor(y * .2422 + 21.94) - (y - 1) / 4, "冬至");
            }
            var lunar = new ChineseLunisolarCalendar();
            for (var date = new DateTime(year, 1, 1); date.Year == year; date = date.AddDays(1))
            {
                if (date < lunar.MinSupportedDateTime || date > lunar.MaxSupportedDateTime) continue;
                int month = lunar.GetMonth(date), day = lunar.GetDayOfMonth(date), leap = lunar.GetLeapMonth(lunar.GetYear(date));
                if (leap != 0 && month == leap) continue; // the leap month repeats no festival
                if (leap != 0 && month > leap) month--;
                string? name = (month, day) switch
                {
                    (1, 1) => "春节", (1, 15) => "元宵节", (2, 2) => "龙抬头", (5, 5) => "端午节", (7, 7) => "七夕", (7, 15) => "中元节",
                    (8, 15) => "中秋节", (9, 9) => "重阳节", (12, 8) => "腊八节", (12, 23) => "小年", _ => null
                };
                if (name != null) list.Add((date, name));
                if (name == "春节" && date.AddDays(-1).Year == year) list.Add((date.AddDays(-1), "除夕"));
            }
            // 除夕 of next year's 春节 can fall on the last day of this year
            var next = new DateTime(year, 12, 31);
            if (next <= lunar.MaxSupportedDateTime && lunar.GetMonth(next.AddDays(1)) == 1 && lunar.GetDayOfMonth(next.AddDays(1)) == 1) list.Add((next, "除夕"));
            if (years.Count > 8) years.Clear();
            return years[year] = list.OrderBy(f => f.Item1).ToList();
        }
    }

    // "今天是中秋节" / "明天是除夕" / "再过 3 天是国庆节"; "" when nothing is near.
    internal static string Days(DateTime local)
    {
        var today = local.Date;
        var near = Festivals(today.Year).Concat(Festivals(today.Year + 1))
            .Select(f => (Days: (int)(f.Date - today).TotalDays, f.Name)).Where(f => f.Days is >= 0 and <= 3).OrderBy(f => f.Days).ToList();
        if (near.Count == 0) return "";
        var parts = near.GroupBy(f => f.Days).Select(g => (g.Key switch { 0 => "今天是", 1 => "明天是", 2 => "后天是", _ => $"再过 {g.Key} 天是" })
            + string.Join("、", g.Select(f => f.Name)));
        return string.Join("，", parts);
    }

    // The 【现在】 line of her context.
    internal static string Describe(DateTime local)
    {
        string week = "日一二三四五六"[(int)local.DayOfWeek].ToString();
        string days = Days(local);
        return $"【现在】{local:yyyy年M月d日} 星期{week} {local:HH:mm}（{Period(local)}，{(Weekend(local) ? "周末" : "工作日")}）"
            + (days.Length > 0 ? $"\n【日子】{days}。可以自然地提一句，不用每次都说。" : "");
    }
}
