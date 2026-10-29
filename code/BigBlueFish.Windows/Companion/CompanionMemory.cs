using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

internal sealed class CompanionMemoryItem
{
    public DateTime CreatedUtc { get; set; }
    public DateTime LastRecalledUtc { get; set; }
    public int Recalls { get; set; }
    public int Importance { get; set; } = 1; // 1 闲聊, 2 计划/情绪/喜好, 3 名字等重要信息
    public string Kind { get; set; } = "chat"; // chat | event
    public string User { get; set; } = "";
    public string Reply { get; set; } = "";
}

// Human-like memory: everything is kept on this computer (data/memory.json), and recall fades
// with time. An item's retention follows a forgetting curve; recalling it makes it last longer.
// When recalled, faint items lose details (numbers, parts of sentences) and their time drifts,
// and some relevant items are not recalled at all.
internal sealed class CompanionMemoryStore
{
    internal const string FileName = "memory.json";
    private const int MaxItems = 300;
    private readonly List<CompanionMemoryItem> items;
    private readonly Random random;
    internal int Count => items.Count;

    private CompanionMemoryStore(List<CompanionMemoryItem> items, Random random)
    {
        this.items = items;
        this.random = random;
    }

    internal static CompanionMemoryStore Load(Random random)
    {
        try
        {
            if (File.Exists(FileName))
            {
                var loaded = JsonSerializer.Deserialize<List<CompanionMemoryItem>>(File.ReadAllText(FileName));
                if (loaded != null) return new CompanionMemoryStore(loaded.Where(i => !string.IsNullOrWhiteSpace(i.User)).ToList(), random);
            }
        }
        catch { }
        return new CompanionMemoryStore(new List<CompanionMemoryItem>(), random);
    }

    internal void Save()
    {
        try
        {
            File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(FileName + ".tmp", FileName, true);
        }
        catch { }
    }

    internal void Clear()
    {
        items.Clear();
        try { if (File.Exists(FileName)) File.Delete(FileName); } catch { }
    }

    internal static int ImportanceOf(string text)
    {
        if (Regex.IsMatch(text, "我叫|叫我|我的名字|我名字|生日|纪念日|我家|我养了|我的猫|我的狗|过敏")) return 3;
        if (Regex.IsMatch(text, "明天|后天|下周|下个月|周末|考试|面试|出差|旅行|旅游|搬家|喜欢|讨厌|害怕|难过|伤心|开心|生病|累|失眠|工作|项目|目标|想要|计划"))
            return 2;
        return 1;
    }

    internal void Remember(string user, string reply, string kind = "chat", int? importance = null)
    {
        var now = DateTime.UtcNow;
        items.Add(new CompanionMemoryItem
        {
            CreatedUtc = now, LastRecalledUtc = now, Kind = kind,
            Importance = importance ?? ImportanceOf(user),
            User = Limit(user.Trim(), 200), Reply = Limit(reply.Trim(), 120)
        });
        Prune(now);
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];

    private static double StabilityDays(CompanionMemoryItem item) =>
        (item.Importance >= 3 ? 30 : item.Importance == 2 ? 5 : 1.5) * (1 + 0.9 * item.Recalls);

    internal static double Retention(CompanionMemoryItem item, DateTime nowUtc) =>
        Math.Exp(-Math.Max(0, (nowUtc - item.LastRecalledUtc).TotalDays) / StabilityDays(item));

    private void Prune(DateTime nowUtc)
    {
        items.RemoveAll(i => Retention(i, nowUtc) < .01 && (nowUtc - i.CreatedUtc).TotalDays > 14);
        if (items.Count > MaxItems)
        {
            var keep = items.OrderByDescending(i => Retention(i, nowUtc) * i.Importance).Take(MaxItems).ToHashSet();
            items.RemoveAll(i => !keep.Contains(i));
        }
    }

    private static readonly HashSet<char> StopChars = new("的了吗呢啊吧呀哦嘛我你他她它是在和就也都还又很太这那个么么什怎样么不没有一上下来去到说要会能对把被给让着过得地啦哈嗯唔诶哎吖噢喔～~，。！？、：；“”‘’（）()[]【】《》…,.!?:; \t\r\n");

    private static HashSet<string> Grams(string text)
    {
        var set = new HashSet<string>();
        var chars = text.ToLowerInvariant().Where(c => !StopChars.Contains(c)).ToArray();
        for (int i = 0; i + 1 < chars.Length; i++) set.Add(new string(chars, i, 2));
        foreach (Match word in Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]{3,}")) set.Add(word.Value);
        return set;
    }

    private static double Relevance(HashSet<string> query, CompanionMemoryItem item)
    {
        if (query.Count == 0) return 0;
        var grams = Grams(item.User + " " + item.Reply);
        if (grams.Count == 0) return 0;
        int shared = query.Count(grams.Contains);
        return shared / Math.Sqrt(query.Count * (double)grams.Count) + (shared >= 2 ? .1 : 0);
    }

    // Returns prompt lines describing what she remembers right now (possibly empty).
    // Recalled items become stronger, so the call has a side effect like real remembering.
    internal IReadOnlyList<string> Recall(string query, DateTime sessionStartUtc)
    {
        var now = DateTime.UtcNow;
        var queryGrams = Grams(query);
        var candidates = new List<(CompanionMemoryItem Item, double Score)>();
        foreach (var item in items)
        {
            if (item.CreatedUtc >= sessionStartUtc) continue; // this session is already in the chat context
            double relevance = Relevance(queryGrams, item);
            if (relevance >= .12) candidates.Add((item, relevance));
        }
        var chosen = candidates.OrderByDescending(c => c.Score).Take(4).Select(c => c.Item).ToList();
        // Important facts (name, likes, plans) may surface even when not mentioned.
        foreach (var item in items.Where(i => i.Importance >= 2 && i.CreatedUtc < sessionStartUtc && !chosen.Contains(i))
                     .OrderByDescending(i => Retention(i, now) * i.Importance).Take(2))
            chosen.Add(item);
        // And sometimes the last thing they talked about before this session.
        var last = items.Where(i => i.CreatedUtc < sessionStartUtc).OrderByDescending(i => i.CreatedUtc).FirstOrDefault();
        if (last != null && !chosen.Contains(last) && random.NextDouble() < .5) chosen.Add(last);

        var lines = new List<string>();
        foreach (var item in chosen)
        {
            double r = Retention(item, now);
            if (r < .03 || random.NextDouble() > r + .12) continue; // forgotten this time
            lines.Add(Describe(item, r, now));
            item.Recalls++;
            item.LastRecalledUtc = now;
        }
        return lines;
    }

    private string Describe(CompanionMemoryItem item, double r, DateTime nowUtc)
    {
        string when = When(nowUtc - item.CreatedUtc, r);
        string what = item.Kind == "event" ? item.User : "主人说过：" + item.User;
        if (r >= .6)
            return $"（记得比较清楚）{when}，{what}" + (item.Kind == "chat" && item.Reply.Length > 0 ? $"；鱼鱼当时回答：{Limit(item.Reply, 40)}" : "");
        if (r >= .3)
            return $"（有点模糊，细节可能记错）大概{when}，{Blur(what, .3)}";
        return $"（只剩一点点印象）好像{when}主人提过一件和「{Snippet(item.User)}」有关的事，具体记不清了";
    }

    private string When(TimeSpan ago, double r)
    {
        double days = ago.TotalDays;
        if (r < .5) days *= .6 + random.NextDouble(); // fuzzy memories misjudge time
        var created = DateTime.Now - TimeSpan.FromDays(Math.Max(0, days));
        if (days < .05) return "刚才";
        if (days < 1 && created.Date == DateTime.Today) return "今天早些时候";
        if (days < 2) return "昨天";
        if (days < 3) return "前天";
        if (days < 7) return $"{(int)days}天前";
        if (days < 14) return "上周";
        if (days < 45) return "几周前";
        if (days < 120) return "一两个月前";
        return "很久以前";
    }

    // Removes details: numbers fade first, then parts of the sentence.
    private string Blur(string text, double amount)
    {
        var builder = new StringBuilder();
        bool dropping = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c)) { if (builder.Length == 0 || builder[^1] != '几') builder.Append('几'); continue; }
            if (!dropping && random.NextDouble() < amount / 6) { dropping = true; builder.Append("……"); continue; }
            if (dropping) { if (random.NextDouble() < .35) dropping = false; continue; }
            builder.Append(c);
        }
        var result = builder.ToString();
        return result.Length > 50 ? result[..50] + "……" : result;
    }

    private string Snippet(string text)
    {
        var clean = new string(text.Where(c => !StopChars.Contains(c)).ToArray());
        if (clean.Length <= 4) return clean.Length == 0 ? "某件小事" : clean;
        int length = Math.Min(clean.Length, 3 + random.Next(2));
        int start = random.Next(clean.Length - length + 1);
        return clean.Substring(start, length);
    }
}
