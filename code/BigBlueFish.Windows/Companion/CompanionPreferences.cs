using System;
using System.IO;
using System.Text.Json;

namespace BigBlueFish.Windows;
internal sealed class CompanionPreferences
{
    public bool ModelEnabled { get; set; } = true;
    public bool GpuEnabled { get; set; } = true;
    public bool PublicInfo { get; set; } = true;
    public bool ForegroundEnabled { get; set; } = true;
    public bool StopModelOnBattery { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool McpEnabled { get; set; }
    public bool PuppetMode { get; set; }
    public bool McpChatEnabled { get; set; }
    public bool McpWakeEnabled { get; set; }
    public bool McpWakeOnText { get; set; } = true;
    public bool McpWakeOnClick { get; set; }
    public bool McpWakeOnDrag { get; set; }
    public string WeatherRegionCode { get; set; } = CompanionRegions.DefaultCode;
    public double Scale { get; set; } = 1;
    public double BubbleScale { get; set; } = 1;
    public int SpeechMinMinutes { get; set; } = 10;
    internal static readonly (int Min, int Max)[] SpeechRanges = [(2, 3), (3, 5), (5, 8), (10, 16), (15, 23), (20, 30)];
    internal int SpeechRangeIndex => Array.FindIndex(SpeechRanges, r => r.Min == NormalizeSpeechMinimum(SpeechMinMinutes));
    internal static int NormalizeSpeechMinimum(int value) => value switch
    {
        2 or 3 or 5 or 10 or 15 or 20 => value,
        <= 2 => 2,
        <= 4 => 3,
        <= 6 => 5,
        <= 12 => 10,
        <= 17 => 15,
        _ => 20
    };
    internal TimeSpan NextSpeechDelay(Random random)
    {
        var range = SpeechRanges[SpeechRangeIndex];
        return TimeSpan.FromMinutes(range.Min + random.NextDouble() * (range.Max - range.Min));
    }
    public int BubbleDurationIndex { get; set; } = 3;
    public int AnimationIntervalSeconds { get; set; } = 40;
    // 0 关闭, 1 偶尔, 2 经常, 3 一直爬（巡游）
    public int CrawlMode { get; set; } = 3;
    // 0 慢, 1 中, 2 快
    public int CrawlSpeedIndex { get; set; } = 1;
    // 互动与关怀
    public bool ChatEnabled { get; set; } = true;
    public bool MemoryEnabled { get; set; } = true;
    public bool WelcomeBack { get; set; } = true;
    public bool SitReminder { get; set; } = true;
    public int SitMinutes { get; set; } = 50;
    public bool WaterReminder { get; set; } = true;
    public int WaterMinutes { get; set; } = 60;
    public bool EyeReminder { get; set; } = true;
    public int EyeMinutes { get; set; } = 45;
    public bool BedtimeReminder { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public bool GameCompanion { get; set; } = true;
    public bool GameScreenReactions { get; set; }
    public string ExtraGames { get; set; } = "";
    public bool DeltaForceReactions { get; set; } = true;
    public bool DeltaForceLog { get; set; } = true;
    public int BreakfastHour { get; set; } = 8;
    public int LunchHour { get; set; } = 12;
    public int DinnerHour { get; set; } = 18;
    public string? LastBrushDate { get; set; }

    internal static readonly int[] BubbleDurations =
    {
        5,
        10,
        15,
        30,
        60,
        120,
        180,
        0
    };
    internal static readonly string[] BubbleDurationLabels =
    {
        "5秒",
        "10秒",
        "15秒",
        "30秒",
        "1分钟",
        "2分钟",
        "3分钟",
        "直至下一句"
    };
    public double? Left { get; set; }
    public double? Top { get; set; }

    public static CompanionPreferences Read()
    {
        foreach (var p in new[]
        {
            "preferences.json",
            "preferences.json.bak"
        }

        )
            try
            {
                var s = JsonSerializer.Deserialize<CompanionPreferences>(File.ReadAllText(p));
                if (s != null)
                {
#if COMPANION_SAFE
                    s.McpEnabled = false;
                    s.PuppetMode = false;
                    s.McpChatEnabled = false;
                    s.McpWakeEnabled = false;
#endif
                    if (!s.McpEnabled) s.PuppetMode = false;
                    s.Scale = double.IsFinite(s.Scale) ? Math.Clamp(s.Scale, .5, 2.0) : 1;
                    if (s.Left.HasValue && !double.IsFinite(s.Left.Value)) s.Left = null;
                    if (s.Top.HasValue && !double.IsFinite(s.Top.Value)) s.Top = null;
                    s.BubbleScale = double.IsFinite(s.BubbleScale) ? Math.Clamp(s.BubbleScale, .5, 2.0) : 1;
                    s.AnimationIntervalSeconds = Math.Clamp(s.AnimationIntervalSeconds, 30, 300);
                    s.CrawlMode = Math.Clamp(s.CrawlMode, 0, 3);
                    s.CrawlSpeedIndex = Math.Clamp(s.CrawlSpeedIndex, 0, 2);
                    s.SitMinutes = Math.Clamp(s.SitMinutes, 15, 180);
                    s.WaterMinutes = Math.Clamp(s.WaterMinutes, 15, 180);
                    s.EyeMinutes = Math.Clamp(s.EyeMinutes, 10, 120);
                    s.FocusMinutes = Math.Clamp(s.FocusMinutes, 5, 120);
                    s.BreakMinutes = Math.Clamp(s.BreakMinutes, 1, 60);
                    s.ExtraGames ??= "";
                    if (s.ExtraGames.Length > 500) s.ExtraGames = s.ExtraGames[..500];
                    s.BreakfastHour = Math.Clamp(s.BreakfastHour, 0, 23);
                    s.LunchHour = Math.Clamp(s.LunchHour, 0, 23);
                    s.DinnerHour = Math.Clamp(s.DinnerHour, 0, 23);
                    s.SpeechMinMinutes = NormalizeSpeechMinimum(s.SpeechMinMinutes);
                    s.BubbleDurationIndex = Math.Clamp(s.BubbleDurationIndex, 0, BubbleDurations.Length - 1);
                    s.WeatherRegionCode = CompanionRegions.District(s.WeatherRegionCode).Code;
                    return s;
                }
            }
            catch
            {
            }

        return new();
    }

    public void Save()
    {
        var t = "preferences.json.tmp";
        File.WriteAllText(t, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists("preferences.json"))
            File.Replace(t, "preferences.json", "preferences.json.bak");
        else
            File.Move(t, "preferences.json");
    }
}
