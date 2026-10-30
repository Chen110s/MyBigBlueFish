using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace BigBlueFish.Windows;

// Game companion: recognises common PC games in the foreground. While the owner plays she stays
// visible (even over borderless fullscreen), lets clicks pass through, skips model speech to keep
// the GPU free, cheers now and then, reminds about long sessions, and can optionally react to
// victory / defeat screens via local OCR.
public partial class MainWindow
{
    private DateTime nextGameCheck = DateTime.MinValue;
    private uint gameCandidatePid;
    private DateTime gameCandidateSince;
    private bool gameActive;
    private bool gameForeground;
    private string gameName = "";
    private DateTime gameStartUtc, gameLastSeenUtc, nextGameCheer, nextGameScreenRead, gameReactionCooldown;
    private int gameHoursNoticed;
    private bool gameScreenReading;
    private bool clickThrough;
    private readonly Dictionary<uint, (string Path, string Name, DateTime Seen)> processInfo = new();

    private bool GameActive => gameActive;

    private static readonly string[] GamePathMarkers =
    [
        @"\steamapps\common\", @"\epic games\", @"\wegame", @"\riot games\", @"\mihoyo\", @"\hoyoplay\",
        @"\genshin impact", @"\star rail", @"\zenlesszonezero", @"\blizzard", @"\battle.net\", @"\ubisoft game launcher\games\",
        @"\ea games\", @"\origin games\", @"\gog games\", @"\gog galaxy\games\", @"\xboxgames\", @"\wuthering waves", @"\hypergryph\",
        @"\minecraft", @"\league of legends\", @"\delta force\"
    ];
    private static readonly string[] GameExeNames =
    [
        "league of legends", "valorant-win64-shipping", "yuanshen", "genshinimpact", "starrail", "zenlesszonezero", "cs2", "csgo",
        "dota2", "overwatch", "tslgame", "r5apex", "fortniteclient-win64-shipping", "robloxplayerbeta", "eldenring", "sekiro",
        "witcher3", "cyberpunk2077", "gta5", "gta5_enhanced", "rdr2", "narakabladepoint", "minecraft.windows", "hearthstone", "wow",
        "diablo iv", "lostark", "pathofexile", "pathofexile_x64", "deltaforceclient-win64-shipping", "wuthering waves",
        "honkai impact 3rd", "bh3", "arknights", "naraka", "eggy party", "sky", "stardew valley", "terraria", "hollow knight",
        "palworld-win64-shipping", "baldur's gate 3", "bg3", "bg3_dx11", "monsterhunterwilds", "monsterhunterworld", "blackmythwukong",
        "b1-win64-shipping", "helldivers2", "apex legends"
    ];
    private static readonly string[] GameCheers =
    [
        "冲呀冲呀～鱼鱼在旁边看着呢！", "{0}好厉害！这一把一定能赢！", "别紧张，深呼吸～{0}可以的！",
        "鱼鱼也想玩……等{0}赢了教鱼鱼好不好？", "加油加油！鱼鱼给{0}摇旗呐喊～", "好帅的操作！鱼鱼都看呆了～"
    ];
    private static readonly string[] GameCheerAnimations = ["轻快摇摆舞", "工作状态-雀跃庆祝", "鲸鱼吐泡泡特效"];
    private static readonly string[] WinLines = ["赢啦！！{0}太强了！鱼鱼要给你放烟花！", "胜利！嘿嘿，鱼鱼就知道{0}最厉害了～", "哇——赢了赢了！奖励{0}一个大大的抱抱！"];
    private static readonly string[] LoseLines = ["没关系没关系，下一把一定能赢！鱼鱼抱抱{0}～", "呜……这把运气不好，休息一下再来吧", "输了也不要紧，{0}已经很努力啦！"];
    private static readonly string[] WinWords = ["胜利", "获胜", "大吉大利", "通关", "VICTORY", "WINNER", "YOU WIN", "PENTAKILL", "五杀", "首胜", "MISSION COMPLETE", "任务完成"];
    private static readonly string[] LoseWords = ["失败", "战败", "你被击败", "你已阵亡", "被淘汰", "DEFEAT", "GAME OVER", "YOU DIED", "MISSION FAILED", "任务失败"];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private (string Path, string Name) ProcessIdentity(uint pid)
    {
        var now = DateTime.UtcNow;
        if (processInfo.TryGetValue(pid, out var cached) && (now - cached.Seen).TotalMinutes < 5) return (cached.Path, cached.Name);
        string path = "", name = "";
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var builder = new StringBuilder(1024);
                uint size = (uint)builder.Capacity;
                if (QueryFullProcessImageName(handle, 0, builder, ref size)) path = builder.ToString();
            }
            finally { CloseHandle(handle); }
        }
        if (path.Length > 0) name = System.IO.Path.GetFileNameWithoutExtension(path);
        else
        {
            try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; } catch { }
        }
        if (processInfo.Count > 64) processInfo.Clear();
        processInfo[pid] = (path, name, now);
        return (path, name);
    }

    private bool IsGameWindow(IntPtr hwnd, uint pid, out string displayName)
    {
        displayName = "";
        var (path, name) = ProcessIdentity(pid);
        if (name.Length == 0) return false;
        string lowerPath = path.ToLowerInvariant(), lowerName = name.ToLowerInvariant();
        var title = new StringBuilder(256);
        CompanionNative.GetWindowText(hwnd, title, title.Capacity);
        string windowTitle = title.ToString().Trim();
        bool game = GamePathMarkers.Any(lowerPath.Contains) || GameExeNames.Contains(lowerName)
            || lowerName == "javaw" && windowTitle.Contains("Minecraft", StringComparison.OrdinalIgnoreCase);
        if (!game && !string.IsNullOrWhiteSpace(preferences.ExtraGames))
        {
            foreach (var entry in preferences.ExtraGames.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var wanted = entry.Trim().ToLowerInvariant();
                if (wanted.EndsWith(".exe")) wanted = wanted[..^4];
                if (wanted.Length > 0 && (lowerName == wanted || lowerPath.Contains(wanted))) { game = true; break; }
            }
        }
        if (!game) return false;
        // Launchers and their store pages are not games.
        if (lowerName is "steam" or "steamwebhelper" or "wegame" or "riotclientservices" or "battle.net" or "hoyoplay" or "unitycrashhandler64"
            || lowerName.Contains("launcher") || lowerName.Contains("crashreport"))
            return false;
        displayName = windowTitle.Length is > 0 and <= 24 ? windowTitle : name;
        return true;
    }

    private void UpdateGame(DateTime now)
    {
        if (now < nextGameCheck) return;
        nextGameCheck = now.AddSeconds(2);
        if (now >= deltaSummaryDue && !gameActive) FinishDeltaSession();
        var hwnd = CompanionNative.GetForegroundWindow();
        CompanionNative.GetWindowThreadProcessId(hwnd, out var pid);
        string name = "";
        bool isGame = hwnd != IntPtr.Zero && pid != 0 && pid != (uint)Environment.ProcessId && IsGameWindow(hwnd, pid, out name);
        bool isDelta = isGame && IsDeltaForceWindow(hwnd, pid);
        if (isGame)
        {
            gameLastSeenUtc = now;
            if (isDelta) deltaLastSeenUtc = now;
            if (!gameActive)
            {
                if (gameCandidatePid != pid) { gameCandidatePid = pid; gameCandidateSince = now; }
                else if ((now - gameCandidateSince).TotalSeconds >= 6) StartGameSession(isDelta ? "三角洲行动" : name, now, isDelta);
            }
            else if (isDelta) gameDeltaForce = true;
        }
        else
        {
            gameCandidatePid = 0;
            if (gameActive && (now - gameLastSeenUtc).TotalSeconds >= 90) EndGameSession(now);
        }
        gameForeground = gameActive && isGame;
        deltaForeground = gameForeground && isDelta;
        SetClickThrough(gameForeground && preferences.GameCompanion);
        if (gameActive) GameTick(now, hwnd);
    }

    private void StartGameSession(string name, DateTime now, bool deltaForce)
    {
        gameActive = true;
        gameName = name;
        gameStartUtc = now;
        gameHoursNoticed = 0;
        gameDeltaForce = deltaForce;
        StartDeltaSession(deltaForce);
        nextDeltaRead = now.AddSeconds(3);
        nextGameCheer = now.AddMinutes(15 + companionRandom.NextDouble() * 10);
        nextGameScreenRead = now.AddSeconds(20);
        if (!preferences.GameCompanion) return;
        if (crawl is { Finished: false } walking) walking.StopRequested = true;
        if (!brain.Busy) brain.Stop(); // free the GPU/RAM the local model holds while the owner plays
        UpdateCompanionVisibility();
        // Coming straight back after a short break (alt-tab, a meal) needs no second welcome.
        if (IsVisible && !PuppetActive && chatWindow == null && (gameEndName != name || (now - gameEndUtc).TotalMinutes >= 10))
            Say(deltaForce ? DeltaGreeting() : $"{bond.Address}要玩《{name}》啦？鱼鱼在旁边给你加油！冲呀～", "工作状态-雀跃庆祝");
    }

    private void EndGameSession(DateTime now)
    {
        var played = gameLastSeenUtc - gameStartUtc;
        string name = gameName;
        bool deltaForce = gameDeltaForce;
        gameActive = false;
        gameForeground = false;
        gameDeltaForce = false;
        deltaForeground = false;
        gameEndUtc = now;
        gameEndName = name;
        SetClickThrough(false);
        if (deltaForce)
        {
            // Between raids people alt-tab or grab a snack: review only once she has been away a while.
            deltaSessionPlayed += played;
            deltaSummaryDue = now.AddMinutes(10);
            return;
        }
        GameGoodbye(name, played);
    }

    private void GameGoodbye(string name, TimeSpan played)
    {
        if (played.TotalMinutes < 10) return;
        string duration = played.TotalMinutes < 60 ? $"{(int)played.TotalMinutes} 分钟" : $"{played.TotalHours:0.#} 个小时";
        AddAffection(1);
        if (preferences.MemoryEnabled && memory != null)
        {
            memory.Remember($"主人玩了《{name}》大约{duration}", "", "event", 1);
            memory.Save();
        }
        if (preferences.GameCompanion && CanChatter)
            Say($"{bond.Address}玩了 {duration} 的《{name}》～赢了吗？眼睛累不累，起来活动一下吧～", "点击回应-元气挥手");
    }

    private void GameTick(DateTime now, IntPtr hwnd)
    {
#if COMPANION_OCR
        // Delta Force keeps its record even while she is hidden (but not with 游戏陪玩 off); she only speaks when visible.
        if (preferences.GameCompanion && gameDeltaForce && preferences.DeltaForceReactions && deltaForeground && now >= nextDeltaRead && !deltaReading)
        {
            nextDeltaRead = now.AddSeconds(6);
            _ = ReadDeltaForceScreen(hwnd);
        }
#endif
        if (!preferences.GameCompanion || PuppetActive || !IsVisible) return;
        int hours = (int)(now - gameStartUtc).TotalHours;
        if (hours >= 1 && hours > gameHoursNoticed)
        {
            gameHoursNoticed = hours;
            Say($"{bond.Address}已经玩了 {hours} 个小时啦～喝口水、眨眨眼睛，再继续吧！");
            return;
        }
        if (now >= nextGameCheer)
        {
            nextGameCheer = now.AddMinutes(18 + companionRandom.NextDouble() * 12);
            if (bubble.Visibility != System.Windows.Visibility.Visible && chatWindow == null)
                Say(Fill(Pick(gameDeltaForce ? DeltaCheers : GameCheers)), Pick(GameCheerAnimations));
        }
#if COMPANION_OCR
        if (preferences.GameScreenReactions && !(gameDeltaForce && preferences.DeltaForceReactions)
            && gameForeground && now >= nextGameScreenRead && !gameScreenReading)
        {
            nextGameScreenRead = now.AddSeconds(15);
            _ = ReadGameScreen(hwnd);
        }
#endif
    }

#if COMPANION_OCR
    private async Task ReadGameScreen(IntPtr hwnd)
    {
        gameScreenReading = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var snapshot = await CompanionOcr.ReadAsync(hwnd, limit.Token);
            if (!gameActive || companionClosed) return;
            string text = string.Join(" ", snapshot.Words.Select(w => w.Text)).ToUpperInvariant();
            string squeezed = text.Replace(" ", "");
            bool win = WinWords.Any(w => squeezed.Contains(w.Replace(" ", "")));
            bool lose = !win && LoseWords.Any(w => squeezed.Contains(w.Replace(" ", "")));
            var now = DateTime.UtcNow;
            if ((!win && !lose) || now < gameReactionCooldown || !IsVisible || PuppetActive) return;
            gameReactionCooldown = now.AddMinutes(3);
            if (win) { AddAffection(.3); Say(Fill(Pick(WinLines)), companionRandom.NextDouble() < .5 ? "放烟花" : "工作状态-雀跃庆祝"); }
            else Say(Fill(Pick(LoseLines)), "点击回应-害羞惊讶");
        }
        catch { } // capture or OCR not available for this game: stay quiet
        finally { gameScreenReading = false; }
    }
#endif

    // While a game is in front she lets mouse clicks pass through so she never steals input.
    private void SetClickThrough(bool enabled)
    {
        if (clickThrough == enabled) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long style = CompanionNative.GetStyle(handle, -20).ToInt64();
        style = enabled ? style | 0x20L : style & ~0x20L;
        CompanionNative.SetStyle(handle, -20, new IntPtr(style));
        clickThrough = enabled;
    }
}
