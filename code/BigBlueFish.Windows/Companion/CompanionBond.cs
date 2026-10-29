using System;
using System.IO;
using System.Text.Json;

namespace BigBlueFish.Windows;

// Affection, daily counters and a few remembered facts about the owner. Stored only in data/bond.json.
internal sealed class CompanionBondState
{
    public double Affection { get; set; } = 30;
    public string? Nickname { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastInteractionUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastFedUtc { get; set; } = DateTime.MinValue;
    public string? LastGreetDate { get; set; }
    public string? BedtimeDate { get; set; }
    public int BedtimeCount { get; set; }
    public double CompanionMinutes { get; set; }
    public string StatsDate { get; set; } = "";
    public int Pats { get; set; }
    public int Feeds { get; set; }
    public int Chats { get; set; }
    public int Pokes { get; set; }
    public int Focus { get; set; }
    public double GainedToday { get; set; }

    internal const string FileName = "bond.json";
    internal const double DailyGainCap = 15;

    internal static CompanionBondState Load()
    {
        try
        {
            if (File.Exists(FileName))
            {
                var state = JsonSerializer.Deserialize<CompanionBondState>(File.ReadAllText(FileName));
                if (state != null)
                {
                    state.Affection = double.IsFinite(state.Affection) ? Math.Clamp(state.Affection, 0, 100) : 30;
                    if (state.Nickname is { Length: > 12 }) state.Nickname = state.Nickname[..12];
                    return state;
                }
            }
        }
        catch { }
        return new CompanionBondState();
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

    // Resets the per-day counters when the local date changes.
    internal void RollDay(DateTime local)
    {
        var date = local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (StatsDate == date) return;
        StatsDate = date;
        Pats = Feeds = Chats = Pokes = Focus = 0;
        GainedToday = 0;
    }

    internal double Add(double amount, DateTime local)
    {
        RollDay(local);
        if (amount > 0)
        {
            amount = Math.Min(amount, Math.Max(0, DailyGainCap - GainedToday));
            GainedToday += amount;
        }
        Affection = Math.Clamp(Affection + amount, 0, 100);
        return amount;
    }

    internal string Level => Affection switch
    {
        < 20 => "初识",
        < 40 => "熟悉",
        < 60 => "亲近",
        < 80 => "亲密",
        _ => "心有灵犀"
    };

    internal string LevelGuidance => Affection switch
    {
        < 20 => "对主人还有点拘谨，礼貌又好奇，偶尔小心翼翼地撒个娇",
        < 40 => "和主人渐渐熟了，说话自然放松，会关心主人",
        < 60 => "很喜欢主人，会撒娇、会主动关心、偶尔小小地黏人",
        < 80 => "非常依恋主人，经常撒娇，什么都想和主人分享",
        _ => "把主人当作最重要的人，温柔又黏人，说话里满是信任和喜欢"
    };

    internal string Address => string.IsNullOrWhiteSpace(Nickname) ? "主人" : Nickname!;
}

public partial class MainWindow
{
    private CompanionBondState bond = new();
    private string? moodOverride;
    private DateTime moodUntil = DateTime.MinValue;
    private DateTime bondSaveDue = DateTime.MaxValue;

    private void LoadBond()
    {
        bond = CompanionBondState.Load();
        bond.RollDay(DateTime.Now);
    }

    private void SaveBondSoon() { if (bondSaveDue == DateTime.MaxValue) bondSaveDue = DateTime.UtcNow.AddSeconds(20); }
    private void SaveBondNow() { bondSaveDue = DateTime.MaxValue; bond.LastSeenUtc = DateTime.UtcNow; bond.Save(); }

    private void AddAffection(double amount)
    {
        bond.Add(amount, DateTime.Now);
        SaveBondSoon();
    }

    private void NoteInteraction()
    {
        bond.LastInteractionUtc = DateTime.UtcNow;
        SaveBondSoon();
    }

    private void SetMood(string mood, TimeSpan duration)
    {
        moodOverride = mood;
        moodUntil = DateTime.UtcNow + duration;
    }

    private bool NearMeal(DateTime local)
    {
        foreach (var hour in new[] { preferences.BreakfastHour, preferences.LunchHour, preferences.DinnerHour })
        {
            double minutes = Math.Abs(local.TimeOfDay.TotalMinutes - hour * 60);
            if (Math.Min(minutes, 1440 - minutes) <= 45) return true;
        }
        return false;
    }

    private string CurrentMood
    {
        get
        {
            var now = DateTime.UtcNow;
            var local = DateTime.Now;
            if (moodOverride != null && now < moodUntil) return moodOverride;
            if (local.Hour >= 23 || local.Hour < 6) return "困倦";
            if (NearMeal(local) && (now - bond.LastFedUtc).TotalHours > 4) return "有点饿";
            if ((now - bond.LastInteractionUtc).TotalHours > 3) return "有点寂寞";
            return bond.Affection >= 60 ? "愉快" : "平静";
        }
    }

    // Shared context line for model prompts.
    private string BondContext()
    {
        var local = DateTime.Now;
        string week = "日一二三四五六"[(int)local.DayOfWeek].ToString();
        string name = string.IsNullOrWhiteSpace(bond.Nickname) ? "" : $"\n主人希望鱼鱼称呼他/她为「{bond.Nickname}」，平时可以这样称呼，也可以叫主人。";
        return $"【现在】{local:yyyy年M月d日} 星期{week} {local:HH:mm}\n" +
               $"【鱼鱼和主人的关系】亲密度：{bond.Level}（{bond.Affection:0}/100）——{bond.LevelGuidance}。{name}\n" +
               $"【鱼鱼此刻的心情】{CurrentMood}";
    }
}
