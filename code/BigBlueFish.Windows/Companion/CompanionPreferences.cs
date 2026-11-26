using System;
using System.IO;
using System.Text.Json;

namespace BigBlueFish.Windows;
internal sealed class CompanionPreferences
{
    // 金价刷新频率：每秒 / 每分钟 / 每十分钟 / 每小时 / 每天; older saved values snap to the nearest of these.
    internal static readonly int[] GoldIntervals = [1, 60, 600, 3600, 86400];

    internal static int GoldInterval(int seconds)
    {
        if (seconds <= 0) return 60;
        int best = 60;
        double distance = double.MaxValue;
        foreach (int option in GoldIntervals)
        {
            double d = Math.Abs(Math.Log(Math.Max(1, seconds)) - Math.Log(option));
            if (d < distance) { distance = d; best = option; }
        }
        return best;
    }

    // 当日涨跌幅提醒: off, or every multiple of this many per cent; a saved value snaps to the nearest of these.
    internal static readonly double[] GoldPercentSteps = [0, 0.5, 1, 1.5, 2, 3, 5];

    internal static double GoldPercentStep(double percent)
    {
        if (!double.IsFinite(percent) || percent <= 0) return 0;
        double best = 0, distance = double.MaxValue;
        foreach (double option in GoldPercentSteps)
            if (option > 0 && Math.Abs(option - percent) < distance) { distance = Math.Abs(option - percent); best = option; }
        return best;
    }

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
    public bool AlwaysOnTop { get; set; } = true;
    public int PetOpacity { get; set; } = 100;
    public bool OpacityHoverRestore { get; set; } = true;
    public bool GameMimic { get; set; } = true;
    public bool MimicHurt { get; set; } = true;
    public int HealKey { get; set; } = 0x35;
    public int BandageKey { get; set; }
    public int InjectKey { get; set; }
    public int PillKey { get; set; }
    public string Skin { get; set; } = "";
    public bool SkinRotate { get; set; }
    public int SkinRotateMinutes { get; set; } = 30;
    public bool SkinRotateWithOriginal { get; set; } = true;
    public bool GoldWatch { get; set; } = true;
    public string GoldVariety { get; set; } = "AU9999";
    public int GoldIntervalSeconds { get; set; } = 60;
    public bool GoldAnnounce { get; set; } = true;
    public double GoldStep { get; set; } = 5;
    public double GoldAlertHigh { get; set; }
    public double GoldAlertLow { get; set; }
    public double GoldSpread { get; set; }
    public bool GoldChangeAlert { get; set; } = true;
    public string GoldAlertLevels { get; set; } = "";
    public double GoldPercentAlert { get; set; } = 2;
    public bool GoldCloseSummary { get; set; } = true;
    public string GoldCloseSummaryDate { get; set; } = "";
    public string GoldPercentSeen { get; set; } = "";
    // 本地智能: the local model chats with the whale-girl persona and action library too
    public bool LocalPersona { get; set; } = true;
    // 帮我做事: commands in the chat box that open programs, search, type, press keys …
    public bool TaskEnabled { get; set; } = true;
    public bool TaskInput { get; set; } = true;
    public bool TaskUseModel { get; set; } = true;
    public int TaskConfirm { get; set; }
    public string TaskSearchEngine { get; set; } = "bing";
    public string TaskAliases { get; set; } = "";
    // From @tuweizhi/dsh-pet: local 碎碎念, memes, work status, throw physics, catching her, staying put, cursors, bubble font.
    public bool WhisperEnabled { get; set; } = true;
    public int WhisperSeconds { get; set; } = 600;
    public string WhisperLines { get; set; } = ""; // one line per line; "" = the 40 built-in lines
    public bool WhisperImages { get; set; } = true;
    public bool ChatMemes { get; set; } = true;
    public int ChatMemeLimit { get; set; } = 10;   // 0 = all
    public System.Collections.Generic.List<string>? MemesOff { get; set; } // null = each meme's default
    public bool WorkStatus { get; set; } = true;
    public double PhysicsGravity { get; set; } = 1400;
    public double PhysicsRestitution { get; set; } = .78;
    public double PhysicsFriction { get; set; } = 2.5;
    public bool PhysicsCeiling { get; set; } = true;
    public double PhysicsPower { get; set; } = 1;
    public bool CatchScore { get; set; } = true;
    public string ScoreDate { get; set; } = "";
    public int ScoreToday { get; set; }
    public int ScoreBest { get; set; }
    public bool StayPut { get; set; }
    public bool GrabCursor { get; set; } = true;
    public string BubbleFont { get; set; } = "";  // "" 乐米小奶泡体, "candy" 上首软糖体
    public bool BubbleClickClose { get; set; } = true;
    // Interaction feel and behaviour (ideas from luweiyabo/dsh-whale-pet).
    public bool HoverFacing { get; set; } = true;
    public bool InteractionFx { get; set; } = true;
    public bool ClickZones { get; set; } = true;
    public bool FlingBall { get; set; } = true;
    public bool EdgeHide { get; set; } = true;
    public bool ClickToMove { get; set; } = true;
    public int ActivityLevel { get; set; } = 1; // 0 安静 · 1 均衡 · 2 活泼
    public bool ChatSensing { get; set; } = true;
    // Feeding and play: she asks for food at meal times, sneaks a snack when starving, and invites you to play.
    public bool MealRequests { get; set; } = true;
    public bool Sneaking { get; set; } = true;
    public bool PlayInvites { get; set; } = true;
    public int TicTacToeLevel { get; set; } = 1; // 井字棋: 0 让着你 · 1 认真 · 2 绝不输
    // 聊天记录: a copy of every chat round (and of DeepSeek 网页版 conversations the owner saves); "" = data\chats.
    public bool ChatLogEnabled { get; set; } = true;
    public string ChatLogFolder { get; set; } = "";
    // DeepSeek 网页版 without the system proxy (DeepSeek only offers WeChat sign-in to mainland China networks).
    public bool DeepSeekDirect { get; set; }
    // 联网聊天: the chat box uses a network model (OpenRouter free models or the DeepSeek API; keys DPAPI-encrypted in data) when it can.
    public bool NetChatEnabled { get; set; } = true;
    public string? NetChatProvider { get; set; }  // "openrouter" (free models, default) or "deepseek" (paid API)
    public string? OpenRouterModels { get; set; } // null = the default list of free models
    public string? DeepSeekModel { get; set; }    // null = deepseek-flash
    public string? DeepSeekPersona { get; set; }  // persona for the network chat; null = MainWindow.DefaultDeepSeekPersona
    // 动作库: action id → pool ("Event", "Click", "Idle", "Disabled") chosen by the owner.
    public System.Collections.Generic.Dictionary<string, string> PoolOverrides { get; set; } = new();
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
                    s.Skin ??= "";
                    s.SkinRotateMinutes = Math.Clamp(s.SkinRotateMinutes, 1, 1440);
                    s.GoldVariety = string.IsNullOrWhiteSpace(s.GoldVariety) ? "AU9999" : s.GoldVariety;
                    s.GoldIntervalSeconds = GoldInterval(s.GoldIntervalSeconds);
                    s.ActivityLevel = Math.Clamp(s.ActivityLevel, 0, 2);
                    s.PoolOverrides ??= new();
                    s.ChatLogFolder ??= "";
                    foreach (var key in System.Linq.Enumerable.ToList(s.PoolOverrides.Keys))
                        if (string.IsNullOrWhiteSpace(key) || s.PoolOverrides[key] is not ("Event" or "Click" or "Idle" or "Disabled")) s.PoolOverrides.Remove(key);
                    s.GoldStep = double.IsFinite(s.GoldStep) ? Math.Clamp(s.GoldStep, .5, 1000) : 5;
                    s.GoldAlertHigh = double.IsFinite(s.GoldAlertHigh) && s.GoldAlertHigh > 0 ? s.GoldAlertHigh : 0;
                    s.GoldAlertLow = double.IsFinite(s.GoldAlertLow) && s.GoldAlertLow > 0 ? s.GoldAlertLow : 0;
                    s.GoldSpread = double.IsFinite(s.GoldSpread) && s.GoldSpread > 0 ? Math.Min(s.GoldSpread, 100000) : 0;
                    s.GoldAlertLevels = CompanionGoldFeed.FormatLevels(CompanionGoldFeed.ParseLevels(s.GoldAlertLevels));
                    s.GoldPercentAlert = GoldPercentStep(s.GoldPercentAlert);
                    s.GoldCloseSummaryDate ??= "";
                    s.GoldPercentSeen ??= "";
                    s.TaskConfirm = Math.Clamp(s.TaskConfirm, 0, 2);
                    s.TaskSearchEngine = System.Linq.Enumerable.Any(CompanionTasks.Engines, e => e.Id == s.TaskSearchEngine) ? s.TaskSearchEngine : "bing";
                    s.TaskAliases = (s.TaskAliases ?? "").Replace("\r", "");
                    if (s.TaskAliases.Length > 6000) s.TaskAliases = s.TaskAliases[..6000];
                    s.WhisperSeconds = Math.Clamp(s.WhisperSeconds, 60, 7200);
                    s.WhisperLines = (s.WhisperLines ?? "").Replace("\r", "");
                    if (s.WhisperLines.Length > 20000) s.WhisperLines = s.WhisperLines[..20000];
                    s.ChatMemeLimit = Math.Clamp(s.ChatMemeLimit, 0, 100);
                    s.PhysicsGravity = double.IsFinite(s.PhysicsGravity) ? Math.Clamp(s.PhysicsGravity, 0, 4000) : 1400;
                    s.PhysicsRestitution = double.IsFinite(s.PhysicsRestitution) ? Math.Clamp(s.PhysicsRestitution, 0, 1) : .78;
                    s.PhysicsFriction = double.IsFinite(s.PhysicsFriction) ? Math.Clamp(s.PhysicsFriction, 0, 10) : 2.5;
                    s.PhysicsPower = double.IsFinite(s.PhysicsPower) ? Math.Clamp(s.PhysicsPower, .3, 3) : 1;
                    s.ScoreDate ??= "";
                    s.ScoreToday = Math.Max(0, s.ScoreToday);
                    s.ScoreBest = Math.Max(0, s.ScoreBest);
                    s.BubbleFont = s.BubbleFont == "candy" ? "candy" : "";
                    s.TicTacToeLevel = Math.Clamp(s.TicTacToeLevel, 0, 2);
                    s.PetOpacity = Math.Clamp(s.PetOpacity, 20, 100);
                    s.HealKey = Math.Clamp(s.HealKey, 0, 255);
                    s.BandageKey = Math.Clamp(s.BandageKey, 0, 255);
                    s.InjectKey = Math.Clamp(s.InjectKey, 0, 255);
                    s.PillKey = Math.Clamp(s.PillKey, 0, 255);
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
