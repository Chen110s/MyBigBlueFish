using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

internal enum TaskKind { Open, Search, Type, Key, Volume, Window, Screenshot, Lock, Settings, Shutdown, CancelShutdown, Clip, Wait }

// One thing she does on the computer. Target / Text / Number mean, per kind:
//   Open: Target = a program's name, a path, a web address or "browser"      Search: Text = the query, Target = the engine ("" = the chosen one)
//   Type: Text                                                               Key: Target = "ctrl+s"
//   Volume: Number = 0–100, or Target = up / down / mute / unmute            Window: Target = desktop / minimize / maximize / restore / close / switch
//   Settings: Target = a page of the Windows settings, "" or "pet"           Shutdown: Number = seconds from now
//   Clip: Text                                                               Wait: Number = seconds
internal sealed record TaskStep(TaskKind Kind, string Target = "", string Text = "", double Number = double.NaN);

// Steps empty and Unknown null: the message is not a command. Unknown set: the part of a command she did not understand.
internal sealed record TaskParse(IReadOnlyList<TaskStep> Steps, string? Unknown);

// 帮我做事: turns what the owner says in the chat box into steps from a fixed list — open a program, a file or a page, search the
// web, type, press keys, volume, windows, a screenshot, lock, settings, timed shutdown. Common wordings are understood by the
// rules below; other wordings can be put into the same steps by the chat model, whose answer is checked against the same list.
// There is no step that runs a command line, deletes anything or clicks inside a program.
internal static class CompanionTasks
{
    internal const int MaxSteps = 8, MaxText = 2000;

    private static readonly Regex Polite = new(@"^(?:(?:鱼鱼|小鱼|鲸鱼少女|请|麻烦|帮我|帮忙|给我|替我|你|能不能|可不可以|可以|能否|我想|我要|现在|马上|立刻|快点?)[，,\s]*)+");
    private static readonly Regex Particles = new(@"(?:[。！!？?～~\s]|吧|啦|呀|哦|嘛|呗|吗|好吗|可以吗|行吗|好不好|谢谢|一下)+$");
    private static readonly Regex Connector = new(@"^[，,；;。\s]*(?:然后|接着|之后|随后|再|并且|并)?[，,\s]*(?:再)?");
    private const string Verbs = "打开|启动|运行|搜索|搜一下|搜一搜|输入|打字|键入|按|把音量|把声音|音量|截图|截屏|截个图|锁屏|最小化|最大化|显示桌面|回到桌面|关闭|关掉|等";
    // where one clause ends and the next begins
    private static readonly Regex Split = new(@"[，,；;。\s]*(?:然后|接着)[，,\s]*|[，,；;。]\s*(?:之后|随后)[，,\s]*|[，,；;。\s]*再(?=" + Verbs + @"|在|用)|[；;\n]+|[，,。]\s*(?=" + Verbs + @"|保存|全选|复制|粘贴|在.{1,12}?(?:里|中|上)|用.{1,8}?(?:搜|查))");

    internal static string Strip(string text) => Polite.Replace(Connector.Replace(text.Trim(), ""), "").Trim();
    // without the polite endings, and without quotation marks around the whole of it
    internal static string Tail(string text)
    {
        text = Particles.Replace(text.Trim(), "").Trim();
        int quote = text.Length > 2 ? "“\"「『《'".IndexOf(text[0]) : -1;
        return quote >= 0 && text[^1] == "”\"」』》'"[quote] ? text[1..^1].Trim() : text;
    }

    // isApp: does this name stand for a program she can open (an alias, a built-in name, a Start menu shortcut)?
    internal static TaskParse Parse(string message, Func<string, bool> isApp)
    {
        var steps = new List<TaskStep>();
        string rest = Strip(message);
        if (rest.Length == 0 || rest.Length > 600) return new TaskParse(steps, null);
        while (rest.Length > 0)
        {
            if (TryType(rest, isApp, out var typed, out string remaining))
            {
                steps.AddRange(typed);
                rest = Strip(remaining);
            }
            else
            {
                var cut = Split.Match(rest);
                string clause = cut.Success && cut.Index > 0 ? rest[..cut.Index] : rest;
                rest = cut.Success && cut.Index > 0 ? Strip(rest[(cut.Index + cut.Length)..]) : "";
                var parsed = ParseClause(Strip(clause), isApp, out string? partly);
                if (parsed == null) return new TaskParse(steps, steps.Count == 0 ? null : clause.Trim());
                steps.AddRange(parsed);
                if (partly != null) return new TaskParse(steps.Take(MaxSteps).ToList(), partly);
            }
            if (steps.Count > MaxSteps) return new TaskParse(steps.Take(MaxSteps).ToList(), "后面的步骤太多了（最多 " + MaxSteps + " 步）");
        }
        // "打开浏览器搜索天气": the search opens the browser by itself
        for (int i = steps.Count - 2; i >= 0; i--)
            if (steps[i] is { Kind: TaskKind.Open, Target: "browser" } && steps[i + 1].Kind == TaskKind.Search) steps.RemoveAt(i);
        return new TaskParse(steps, null);
    }

    // "在记事本里写：……" (the program must be one she knows) and "输入：……". The text runs to the end of the message, or to the
    // closing quotation mark when it is quoted — after that the command may go on.
    private static readonly Regex TypeInApp = new(@"^(?:在|用|往)(?<app>.{1,20}?)(?:里面|里|中|上)?(?:写下|写上|写|输入|打字|打上|记下|记录)(?:一段话|一句话|一行字?|下|上)?[:：，,\s]*(?<text>.+)$", RegexOptions.Singleline);
    private static readonly Regex TypeBare = new(@"^(?:输入|打字|键入|打出)(?:文字|内容|一段话|一句话)?(?:[:：]\s*|\s+|(?=[“""「『]))(?<text>.+)$", RegexOptions.Singleline);
    private const string OpenQuotes = "“\"「『", CloseQuotes = "”\"」』";

    private static bool TryType(string rest, Func<string, bool> isApp, out List<TaskStep> steps, out string remaining)
    {
        steps = new List<TaskStep>();
        remaining = "";
        string text;
        var inApp = TypeInApp.Match(rest);
        if (inApp.Success && isApp(Tail(inApp.Groups["app"].Value)))
        {
            steps.Add(new TaskStep(TaskKind.Open, Tail(inApp.Groups["app"].Value)));
            text = inApp.Groups["text"].Value;
        }
        else if (TypeBare.Match(rest) is { Success: true } bare) text = bare.Groups["text"].Value;
        else return false;
        text = text.Trim();
        int quote = text.Length > 1 ? OpenQuotes.IndexOf(text[0]) : -1;
        int close = quote >= 0 ? text.IndexOf(CloseQuotes[quote], 1) : -1;
        if (close > 1)
        {
            remaining = text[(close + 1)..];
            text = text[1..close];
        }
        else
        {
            // not quoted: the text ends where the rest of the sentence is commands she understands ("……，然后保存")
            foreach (Match cut in Split.Matches(text))
            {
                if (cut.Index == 0) continue;
                var tail = Parse(text[(cut.Index + cut.Length)..], isApp);
                if (tail.Steps.Count == 0 || tail.Unknown != null) continue;
                remaining = text[(cut.Index + cut.Length)..];
                text = text[..cut.Index];
                break;
            }
        }
        if (text.Length == 0 || text.Length > MaxText) { steps.Clear(); return false; }
        steps.Add(new TaskStep(TaskKind.Type, Text: text));
        return true;
    }

    private static readonly Regex SearchWith = new(@"^(?:用|在|上|去|到)?(?<engine>百度|必应|bing|谷歌|google|搜狗|浏览器|网上|网页)(?:上|里)?(?:搜索|搜一下|搜一搜|搜搜|搜|查一下|查查|查询|查)(?:一下)?[:：\s]*(?<q>.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex SearchBare = new(@"^(?:上网)?(?:搜索|搜一下|搜一搜|搜搜)(?:一下)?[:：\s]*(?<q>.+)$|^上网(?:搜|查一下|查)[:：\s]*(?<q>.+)$");
    private static readonly Regex SearchOnce = new(@"^(?<engine>百度|必应|谷歌)一下[:：\s]*(?<q>.+)$");
    private static readonly Regex VolumeSet = new(@"^(?:把)?(?:电脑|系统)?的?(?:音量|声音)(?:调到|调成|调整到|调整为|设为|设成|设置为|设置成|开到|到)\s*(?:百分之)?(?<n>\d{1,3})\s*%?$");
    private static readonly Regex VolumeUp = new(@"^(?:把)?(?:音量|声音)(?:调大|调高|加大|增大|开大)(?:一点|点|一些)?$|^(?:音量|声音)(?:大一点|大点|大一些|高一点)$|^(?:调大|调高|加大|增大|提高)(?:一点|点)?(?:音量|声音)$");
    private static readonly Regex VolumeDown = new(@"^(?:把)?(?:音量|声音)(?:调小|调低|减小|降低|关小)(?:一点|点|一些)?$|^(?:音量|声音)(?:小一点|小点|小一些|低一点)$|^(?:调小|调低|减小|降低)(?:一点|点)?(?:音量|声音)$");
    private static readonly Regex Mute = new(@"^(?:电脑|系统)?静音$|^(?:把)?(?:声音|音量)关(?:掉|了)$");
    private static readonly Regex Unmute = new(@"^(?:取消|解除|关闭)静音$|^(?:打开|恢复)声音$");
    private static readonly Regex Desktop = new(@"^(?:显示|回到?|返回)桌面$");
    private static readonly Regex Minimize = new(@"^最小化(?:当前|这个)?(?:窗口)?$|^把(?:当前|这个)?窗口最小化$");
    private static readonly Regex Maximize = new(@"^最大化(?:当前|这个)?(?:窗口)?$|^把(?:当前|这个)?窗口最大化$");
    private static readonly Regex Restore = new(@"^还原(?:当前|这个)?窗口$");
    private static readonly Regex CloseWindow = new(@"^(?:关闭|关掉)(?:当前|这个)(?:窗口|程序|软件)?$|^(?:关闭|关掉)窗口$");
    private static readonly Regex SwitchWindow = new(@"^切换(?:一下)?窗口$|^切换?到上一个窗口$");
    private static readonly Regex Shot = new(@"^截(?:个|一下|张|一张)?(?:图|屏)$|^(?:屏幕|全屏)截图$");
    private static readonly Regex LockPc = new(@"^锁屏$|^锁定?(?:电脑|屏幕)$");
    private static readonly Regex ShutdownIn = new(@"^(?<n>\d{1,4})\s*(?<u>秒|分钟|分|个小时|小时)(?:钟)?(?:后|之后|以后)(?:自动)?关机$|^定时关机[:：\s]*(?<n>\d{1,4})\s*(?<u>秒|分钟|分|个小时|小时)$|^定时\s*(?<n>\d{1,4})\s*(?<u>秒|分钟|分|个小时|小时)(?:后)?关机$");
    private static readonly Regex ShutdownNow = new(@"^(?:电脑)?关机$|^关(?:闭)?电脑$");
    private static readonly Regex ShutdownCancel = new(@"^取消(?:定时|自动)?关机$|^不关机了?$");
    private static readonly Regex WaitFor = new(@"^等(?:待)?\s*(?<n>\d{1,2})\s*秒(?:钟)?$");
    private static readonly Regex ClipText = new(@"^把(?<t>.+)复制到剪贴板$", RegexOptions.Singleline);
    private static readonly Regex Press = new(@"^按(?:一下|下)?\s*(?<k>.+?)\s*键?$");
    private static readonly Regex OpenSettings = new(@"^(?:打开|进入|去)(?<p>.{0,8}?)的?设置(?:页面|页|界面)?$");
    private static readonly Regex OpenThing = new(@"^(?<verb>打开|启动|运行|开启|开一下|开)(?:一下)?\s*(?<x>.+)$", RegexOptions.Singleline);
    private static readonly Regex NotAName = new(@"^[得的了着过不很吗]|吗|什么|怎么|为什么|[？?！!。：:，,“”""「」『』]|\s{2,}|输入|打字|搜索|然后");
    private static readonly Dictionary<string, string> Shortcuts = new()
    {
        ["全选"] = "ctrl+a", ["复制"] = "ctrl+c", ["粘贴"] = "ctrl+v", ["剪切"] = "ctrl+x", ["撤销"] = "ctrl+z", ["保存"] = "ctrl+s", ["回车"] = "enter", ["换行"] = "enter"
    };

    private static List<TaskStep>? One(TaskStep step) => new() { step };

    // unknown: the rest of the clause after a part she did understand ("打开记事本" + "写点东西")
    private static List<TaskStep>? ParseClause(string clause, Func<string, bool> isApp, out string? unknown)
    {
        unknown = null;
        if (clause.Length == 0) return null;
        foreach (var search in new[] { SearchWith.Match(clause), SearchOnce.Match(clause), SearchBare.Match(clause) })
        {
            if (!search.Success) continue;
            string query = search.Groups["q"].Value.Trim().TrimEnd('。', '！', '!', '？', '?');
            return query.Length is 0 or > 200 ? null : One(new TaskStep(TaskKind.Search, Engine(search.Groups["engine"].Value), query));
        }
        string t = Tail(clause);
        if (VolumeSet.Match(t) is { Success: true } set)
            return int.Parse(set.Groups["n"].Value, CultureInfo.InvariantCulture) is var level and <= 100 ? One(new TaskStep(TaskKind.Volume, Number: level)) : null;
        if (VolumeUp.IsMatch(t)) return One(new TaskStep(TaskKind.Volume, "up"));
        if (VolumeDown.IsMatch(t)) return One(new TaskStep(TaskKind.Volume, "down"));
        if (Mute.IsMatch(t)) return One(new TaskStep(TaskKind.Volume, "mute"));
        if (Unmute.IsMatch(t)) return One(new TaskStep(TaskKind.Volume, "unmute"));
        if (Desktop.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "desktop"));
        if (Minimize.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "minimize"));
        if (Maximize.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "maximize"));
        if (Restore.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "restore"));
        if (CloseWindow.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "close"));
        if (SwitchWindow.IsMatch(t)) return One(new TaskStep(TaskKind.Window, "switch"));
        if (Shot.IsMatch(t)) return One(new TaskStep(TaskKind.Screenshot));
        if (LockPc.IsMatch(t)) return One(new TaskStep(TaskKind.Lock));
        if (ShutdownCancel.IsMatch(t)) return One(new TaskStep(TaskKind.CancelShutdown));
        if (ShutdownIn.Match(t) is { Success: true } timed)
        {
            int amount = int.Parse(timed.Groups["n"].Value, CultureInfo.InvariantCulture);
            string unit = timed.Groups["u"].Value;
            return ShutdownStep(amount * (unit == "秒" ? 1.0 : unit.Contains('时') ? 3600 : 60));
        }
        if (ShutdownNow.IsMatch(t)) return ShutdownStep(60);
        if (WaitFor.Match(t) is { Success: true } wait)
            return int.Parse(wait.Groups["n"].Value, CultureInfo.InvariantCulture) is var seconds and >= 1 and <= 30 ? One(new TaskStep(TaskKind.Wait, Number: seconds)) : null;
        if (ClipText.Match(clause) is { Success: true } clip && clip.Groups["t"].Value.Trim().Length is > 0 and <= MaxText)
            return One(new TaskStep(TaskKind.Clip, Text: clip.Groups["t"].Value.Trim().Trim('「', '」', '“', '”', '"')));
        if (Shortcuts.TryGetValue(t, out var combo)) return One(new TaskStep(TaskKind.Key, combo));
        if (Press.Match(t) is { Success: true } press) return TryKeys(press.Groups["k"].Value, out string keys) ? One(new TaskStep(TaskKind.Key, keys)) : null;
        if (OpenSettings.Match(t) is { Success: true } settings)
        {
            string page = settings.Groups["p"].Value.Trim();
            if (page is "鱼鱼" or "桌宠" or "你" or "鲸鱼少女") return One(new TaskStep(TaskKind.Settings, "pet"));
            if (page.Length == 0 || Regex.IsMatch(page, "^(系统|电脑|windows)$", RegexOptions.IgnoreCase)) return One(new TaskStep(TaskKind.Settings));
            if (SettingsUri(page) != null) return One(new TaskStep(TaskKind.Settings, page));
        }
        return OpenThing.Match(t) is { Success: true } open ? ParseOpen(open.Groups["verb"].Value, open.Groups["x"].Value, isApp, out unknown) : null;
    }

    // at least a minute (time to change one's mind), at most a day
    private static List<TaskStep>? ShutdownStep(double seconds) =>
        seconds > 86400 ? null : One(new TaskStep(TaskKind.Shutdown, Number: Math.Max(60, seconds)));

    private static List<TaskStep>? ParseOpen(string verb, string what, Func<string, bool> isApp, out string? unknown)
    {
        unknown = null;
        string x = Tail(what);
        if (x.Length == 0 || x.Length > 260) return null;
        var address = Regex.Match(x, @"^(?:网址|网站|网页|链接)[:：\s]*(?<u>\S+)$");
        if (address.Success) return NormalizeUrl(address.Groups["u"].Value) is { } url ? One(new TaskStep(TaskKind.Open, url)) : null;
        if (NormalizeUrl(x) is { } direct && LooksLikeUrl(x)) return One(new TaskStep(TaskKind.Open, direct));
        if (NormalizePath(x) is { } path) return One(new TaskStep(TaskKind.Open, path));
        if (x is "浏览器" or "默认浏览器" or "网页浏览器") return One(new TaskStep(TaskKind.Open, "browser"));
        string? Known(string name)
        {
            name = Tail(name);
            if (name.Length == 0) return null;
            if (isApp(name)) return name;
            string bare = Regex.Replace(name, "(软件|程序|应用|客户端|app)$", "", RegexOptions.IgnoreCase).Trim();
            return bare.Length > 0 && bare != name && isApp(bare) ? bare : null;
        }
        if (Known(x) is { } known) return One(new TaskStep(TaskKind.Open, known));
        // "打开微信和记事本": every part has to be a program she knows
        var parts = Regex.Split(x, "和|跟|与|、|,|，|以及|还有").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count is > 1 and <= 4 && parts.All(p => Known(p) != null)) return parts.Select(p => new TaskStep(TaskKind.Open, Known(p)!)).ToList();
        // "打开记事本输入“……”再按回车", said without a pause: a program she knows, followed by commands she understands
        for (int length = Math.Min(x.Length - 1, 20); length >= 2; length--)
        {
            string head = x[..length];
            var first = head is "浏览器" or "默认浏览器" ? new TaskStep(TaskKind.Open, "browser") : isApp(head) ? new TaskStep(TaskKind.Open, head) : null;
            if (first == null) continue;
            var then = Parse(x[length..], isApp);
            unknown = then.Steps.Count == 0 ? x[length..].Trim() : then.Unknown;
            return new List<TaskStep> { first }.Concat(then.Steps).ToList();
        }
        // A short name after 打开 / 启动 / 运行 that she does not know is still a command ("没找到这个软件"); 开 / 开启 are too
        // common in ordinary talk (开心, 开启新的一天) for that.
        bool strict = verb is "开" or "开启" or "开一下";
        return !strict && x.Length <= 12 && !NotAName.IsMatch(x) ? One(new TaskStep(TaskKind.Open, x)) : null;
    }

    // ---------------------------------------------------------------- web addresses, paths, search
    internal static bool LooksLikeUrl(string text) =>
        Regex.IsMatch(text.Trim(), @"^(?:https?://\S+|(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|cn|net|org|edu|gov|io|cc|tv|me|info|top|xyz|dev|app|co|hk|tw|jp|uk|de)(?:[/?#:]\S*)?)$", RegexOptions.IgnoreCase);

    // http(s) only; a bare host gets https://
    internal static string? NormalizeUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 2000 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)) return null;
        if (!Regex.IsMatch(text, "^https?://", RegexOptions.IgnoreCase))
        {
            if (text.Contains("://") || !LooksLikeUrl(text)) return null;
            text = "https://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri.AbsoluteUri : null;
    }

    // "D:\资料\a.docx", "\\server\share", "D盘"
    internal static string? NormalizePath(string text)
    {
        text = text.Trim().Trim('"');
        var drive = Regex.Match(text, @"^(?<d>[A-Za-z])\s*盘$");
        if (drive.Success) return drive.Groups["d"].Value.ToUpperInvariant() + @":\";
        return Regex.IsMatch(text, @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/])") && text.IndexOfAny(new[] { '<', '>', '|', '*', '?', '"' }) < 0 ? text.Replace('/', '\\') : null;
    }

    // Files she will not open: scripts, installers and the like run code or change the system when opened.
    internal static bool BlockedFile(string path) =>
        Regex.IsMatch(path.Trim(), @"\.(bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|msi|msp|reg|scr|com|pif|hta|cpl|jar|inf|msc|application|gadget)$", RegexOptions.IgnoreCase);

    internal static bool IsProgramFile(string target) => Regex.IsMatch(target.Trim(), @"\.exe$", RegexOptions.IgnoreCase) && NormalizePath(target) != null;

    private static string Engine(string word) => word.ToLowerInvariant() switch
    {
        "百度" => "baidu", "必应" or "bing" => "bing", "谷歌" or "google" => "google", "搜狗" => "sogou", _ => ""
    };

    internal static readonly (string Id, string Name, string Home, string Query)[] Engines =
    [
        ("bing", "必应", "https://www.bing.com/", "https://www.bing.com/search?q="),
        ("baidu", "百度", "https://www.baidu.com/", "https://www.baidu.com/s?wd="),
        ("google", "谷歌", "https://www.google.com/", "https://www.google.com/search?q="),
        ("sogou", "搜狗", "https://www.sogou.com/", "https://www.sogou.com/web?query=")
    ];

    internal static (string Id, string Name, string Home, string Query) EngineOf(string id, string preferred)
    {
        foreach (var engine in Engines) if (engine.Id == (id.Length > 0 ? id : preferred)) return engine;
        return Engines[0];
    }

    internal static string SearchUrl(string engine, string preferred, string query) => EngineOf(engine, preferred).Query + Uri.EscapeDataString(query);

    // ---------------------------------------------------------------- keys
    // name → virtual-key code; the first four are the modifiers
    internal static readonly Dictionary<string, ushort> Keys = BuildKeys();

    private static Dictionary<string, ushort> BuildKeys()
    {
        var keys = new Dictionary<string, ushort>
        {
            ["ctrl"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
            ["enter"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09, ["esc"] = 0x1B, ["backspace"] = 0x08, ["delete"] = 0x2E, ["insert"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27
        };
        for (char c = 'a'; c <= 'z'; c++) keys[c.ToString()] = (ushort)(c - 'a' + 0x41);
        for (char c = '0'; c <= '9'; c++) keys[c.ToString()] = (ushort)(c - '0' + 0x30);
        for (int f = 1; f <= 12; f++) keys["f" + f] = (ushort)(0x70 + f - 1);
        return keys;
    }

    private static readonly Dictionary<string, string> KeyWords = new()
    {
        ["control"] = "ctrl", ["控制"] = "ctrl", ["上档"] = "shift", ["换挡"] = "shift", ["windows"] = "win", ["徽标"] = "win", ["开始"] = "win",
        ["回车"] = "enter", ["确认"] = "enter", ["return"] = "enter", ["空格"] = "space", ["制表"] = "tab", ["escape"] = "esc", ["退出"] = "esc",
        ["退格"] = "backspace", ["删除"] = "delete", ["del"] = "delete", ["上"] = "up", ["下"] = "down", ["左"] = "left", ["右"] = "right",
        ["pgup"] = "pageup", ["pgdn"] = "pagedown", ["向上"] = "up", ["向下"] = "down", ["向左"] = "left", ["向右"] = "right"
    };

    // "Ctrl+S", "ctrl 加 s", "回车" → "ctrl+s", "enter". Up to three modifiers and one key (or the Windows key alone); the Run
    // box (Win+R) is not offered — nothing she does may end in a command line.
    internal static bool TryKeys(string text, out string combo)
    {
        combo = "";
        var tokens = Regex.Split(text.Trim().ToLowerInvariant().Replace("方向键", "").Replace("箭头", ""), @"\s*(?:\+|＋|加|-|\s)\s*")
            .Select(token => token.Trim().TrimEnd('键')).Where(token => token.Length > 0).Select(token => KeyWords.GetValueOrDefault(token, token)).ToList();
        if (tokens.Count is 0 or > 4 || tokens.Any(token => !Keys.ContainsKey(token)) || tokens.Distinct().Count() != tokens.Count) return false;
        var modifiers = tokens.Where(IsModifier).ToList();
        var others = tokens.Where(token => !IsModifier(token)).ToList();
        if (others.Count > 1 || others.Count == 0 && !(tokens.Count == 1 && tokens[0] == "win")) return false;
        var ordered = new[] { "ctrl", "alt", "shift", "win" }.Where(modifiers.Contains).Concat(others).ToList();
        combo = string.Join("+", ordered);
        return combo != "win+r";
    }

    internal static bool IsModifier(string key) => key is "ctrl" or "shift" or "alt" or "win";

    internal static string PrettyKeys(string combo) =>
        string.Join("+", combo.Split('+').Select(key => key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..]));

    // ---------------------------------------------------------------- Windows settings pages
    private static readonly (string Words, string Uri)[] SettingsPages =
    [
        ("蓝牙", "ms-settings:bluetooth"), ("wifi|wi-fi|无线|无线网络|wlan", "ms-settings:network-wifi"), ("网络|网络和internet|上网", "ms-settings:network"),
        ("显示|显示器|屏幕|分辨率", "ms-settings:display"), ("声音|音频", "ms-settings:sound"), ("通知", "ms-settings:notifications"),
        ("电源|电池|电源和睡眠", "ms-settings:powersleep"), ("存储|磁盘", "ms-settings:storagesense"), ("应用|应用和功能|程序", "ms-settings:appsfeatures"),
        ("默认应用", "ms-settings:defaultapps"), ("个性化|壁纸|背景|主题", "ms-settings:personalization"), ("更新|windows更新|系统更新", "ms-settings:windowsupdate"),
        ("鼠标", "ms-settings:mousetouchpad"), ("键盘|输入法", "ms-settings:keyboard"), ("打印机|打印", "ms-settings:printers"), ("日期|时间|日期和时间", "ms-settings:dateandtime"),
        ("语言", "ms-settings:regionlanguage"), ("关于|系统信息", "ms-settings:about"), ("夜间模式|夜灯", "ms-settings:nightlight"), ("代理", "ms-settings:network-proxy")
    ];

    // null: not a page she knows; "" is the front page
    internal static string? SettingsUri(string page)
    {
        page = page.Trim().ToLowerInvariant();
        if (page.Length == 0) return "ms-settings:";
        foreach (var (words, uri) in SettingsPages)
            if (words.Split('|').Contains(page)) return uri;
        return null;
    }

    // ---------------------------------------------------------------- programs
    // Built-in names. "exe:" = a program of Windows itself, "path:" = looked up under App Paths in the registry, "uri:" = a
    // protocol Windows knows, "browser" = the default browser.
    private static readonly (string Names, string Target)[] BuiltIn =
    [
        ("记事本|notepad", "exe:notepad.exe"), ("计算器|calc", "exe:calc.exe"), ("画图|mspaint", "exe:mspaint.exe"),
        ("资源管理器|文件管理器|文件资源管理器|此电脑|我的电脑|explorer", "exe:explorer.exe"), ("任务管理器", "exe:taskmgr.exe"), ("控制面板", "exe:control.exe"),
        ("命令提示符|cmd", "exe:cmd.exe"), ("截图工具", "uri:ms-screenclip:"), ("应用商店|微软商店|商店", "uri:ms-windows-store:"), ("照片", "uri:ms-photos:"),
        ("时钟|闹钟", "uri:ms-clock:"), ("浏览器|默认浏览器", "browser"), ("word", "path:winword.exe"), ("excel", "path:excel.exe"),
        ("powerpoint|ppt", "path:powerpnt.exe"), ("edge|edge浏览器", "path:msedge.exe"), ("chrome|谷歌浏览器", "path:chrome.exe")
    ];

    internal static string Normal(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s　]+", "");

    internal static string? BuiltInTarget(string name)
    {
        string wanted = Normal(name);
        foreach (var (names, target) in BuiltIn)
            if (names.Split('|').Contains(wanted)) return target;
        return null;
    }

    // "微信=D:\Program Files\Tencent\WeChat\WeChat.exe", one per line
    internal static Dictionary<string, string> ParseAliases(string? text)
    {
        var aliases = new Dictionary<string, string>();
        foreach (string line in (text ?? "").Split('\n'))
        {
            int at = line.IndexOfAny(new[] { '=', '＝' });
            if (at <= 0) continue;
            string name = Normal(line[..at]), target = line[(at + 1)..].Trim().Trim('"');
            if (name.Length > 0 && target.Length > 0 && aliases.Count < 50) aliases[name] = target;
        }
        return aliases;
    }

    private static readonly Regex NotAProgram = new("卸载|uninstall|帮助|help|readme|说明|官网|网站|website|修复|repair|许可|license|更新日志|反馈", RegexOptions.IgnoreCase);

    // The Start menu / desktop shortcut that a spoken name means: the same name, else one that begins with it (or it with one),
    // else one that contains it; the shortest of the best kind. Uninstallers, help files and the like are never meant.
    internal static string? MatchShortcut(IEnumerable<(string Name, string Path)> shortcuts, string name)
    {
        string wanted = Normal(name);
        if (wanted.Length == 0) return null;
        (int Rank, int Length, string Path) best = (int.MaxValue, 0, "");
        foreach (var (shortcut, path) in shortcuts)
        {
            string have = Normal(shortcut);
            if (have.Length == 0 || NotAProgram.IsMatch(shortcut) && !NotAProgram.IsMatch(name)) continue;
            // one character is too little to go by, except as the whole name
            int rank = have == wanted ? 0
                : wanted.Length >= 2 && have.StartsWith(wanted, StringComparison.Ordinal) || have.Length >= 2 && wanted.StartsWith(have, StringComparison.Ordinal) ? 1
                : wanted.Length >= 2 && have.Contains(wanted, StringComparison.Ordinal) ? 2 : -1;
            if (rank >= 0 && (rank, have.Length).CompareTo((best.Rank, best.Length)) < 0) best = (rank, have.Length, path);
        }
        return best.Rank == int.MaxValue ? null : best.Path;
    }

    // ---------------------------------------------------------------- saying what will be done, and what needs a yes first
    private static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    internal static string Describe(TaskStep step, string preferredEngine = "bing") => step.Kind switch
    {
        TaskKind.Open => step.Target == "browser" ? "打开浏览器" : NormalizeUrl(step.Target) != null && LooksLikeUrl(step.Target) || step.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? "打开网页 " + Short(step.Target, 60) : NormalizePath(step.Target) != null ? "打开 " + Short(step.Target, 60) : $"打开「{step.Target}」",
        TaskKind.Search => $"用{EngineOf(step.Target, preferredEngine).Name}搜索「{Short(step.Text, 40)}」",
        TaskKind.Type => $"输入文字（{step.Text.Length} 个字）：{Short(step.Text.Replace("\r", "").Replace('\n', '↵'), 40)}",
        TaskKind.Key => "按 " + PrettyKeys(step.Target),
        TaskKind.Volume => step.Target switch { "up" => "调大音量", "down" => "调小音量", "mute" => "静音", "unmute" => "取消静音", _ => $"把音量调到 {step.Number:0}%" },
        TaskKind.Window => step.Target switch
        {
            "desktop" => "显示桌面", "minimize" => "最小化当前窗口", "maximize" => "最大化当前窗口", "restore" => "还原当前窗口", "close" => "关闭当前窗口", _ => "切换到上一个窗口"
        },
        TaskKind.Screenshot => "截一张全屏图，存到「图片\\鱼鱼截图」",
        TaskKind.Lock => "锁定电脑",
        TaskKind.Settings => step.Target == "pet" ? "打开鱼鱼的设置" : step.Target.Length == 0 ? "打开 Windows 设置" : $"打开 Windows 设置的「{step.Target}」",
        TaskKind.Shutdown => (step.Number >= 3600 ? $"{step.Number / 3600:0.#} 小时" : step.Number >= 120 ? $"{step.Number / 60:0.#} 分钟" : $"{step.Number:0} 秒") + "后关机（说「取消关机」可以撤销）",
        TaskKind.CancelShutdown => "取消定时关机",
        TaskKind.Clip => "把文字复制到剪贴板：" + Short(step.Text, 40),
        _ => $"等 {step.Number:0} 秒"
    };

    internal static string Describe(IReadOnlyList<TaskStep> steps, string preferredEngine = "bing") =>
        steps.Count == 1 ? Describe(steps[0], preferredEngine) : string.Join("\n", steps.Select((step, i) => $"{i + 1}. {Describe(step, preferredEngine)}"));

    // Uses the keyboard, closes something, turns the computer off or starts a program file given by its path.
    internal static bool Risky(TaskStep step) =>
        step.Kind is TaskKind.Type or TaskKind.Key or TaskKind.Shutdown || step.Kind == TaskKind.Window && step.Target == "close"
        || step.Kind == TaskKind.Open && IsProgramFile(step.Target);

    internal static bool UsesInput(TaskStep step) => step.Kind is TaskKind.Type or TaskKind.Key;

    // mode: 0 = only what is risky, 1 = always, 2 = never. A shutdown and steps worked out by the model are always shown first.
    internal static bool NeedsConfirm(IReadOnlyList<TaskStep> steps, int mode, bool fromModel) =>
        fromModel || steps.Any(step => step.Kind == TaskKind.Shutdown) || mode == 1 || mode == 0 && steps.Any(Risky);

    // ---------------------------------------------------------------- wordings the rules do not know: the chat model
    // Worth asking the model: the sentence starts the way a command does.
    private static readonly Regex TaskLike = new(@"^(?:打开|启动|运行|关闭|关掉|搜索|搜一下|搜搜|上网|百度|输入|打字|按(?:一下|下)?\s*[A-Za-z]|把(?:音量|声音|窗口|电脑|.{1,20}?(?:打开|关掉|关闭|复制|最小化|最大化|调到|调成))|调(?:大|小|高|低)|截图|截屏|截个|锁屏|锁定|最小化|最大化|显示桌面|切换(?:窗口|到)|新建|定时关机|音量|声音|静音|用.{1,10}?(?:打开|搜|查|写|算|画|播放|看)|在.{1,12}?(?:里|中|上)(?:写|输入|搜|查|打开|新建))");

    internal static bool LooksLikeTask(string message)
    {
        string text = Strip(message);
        return text.Length is >= 2 and <= 120 && TaskLike.IsMatch(text);
    }

    internal const string PlannerPrompt =
        "你把主人的一句话变成电脑操作步骤，只输出一个 JSON 对象，不要解释，不要 Markdown。\n"
        + "格式：{\"steps\":[{\"do\":\"动作\",\"target\":\"…\",\"text\":\"…\",\"number\":数字}]}，最多 8 步，用不到的字段不要写。\n"
        + "只能用这些动作：\n"
        + "open：打开软件、文件、文件夹或网页，target 写软件名（如 记事本、微信）、完整路径或网址；target 写 browser 表示打开浏览器\n"
        + "search：用浏览器搜索，text 写搜索词，target 可写 baidu / bing / google\n"
        + "type：在刚打开的软件或主人正在用的窗口里输入文字，text 写要输入的原文\n"
        + "key：按键，target 如 ctrl+s、enter、alt+tab\n"
        + "volume：number 写 0–100 的音量，或 target 写 up / down / mute / unmute\n"
        + "window：target 写 desktop / minimize / maximize / restore / close / switch\n"
        + "screenshot：截全屏图    lock：锁屏    settings：打开 Windows 设置，target 写页面名（蓝牙、网络、显示、声音……）\n"
        + "shutdown：number 写几分钟后关机    cancel_shutdown：取消关机    clip：把 text 复制到剪贴板    wait：等 number 秒\n"
        + "规则：不能点击软件里的按钮，不能读屏幕，不能删除文件，不能运行命令行；主人没说的事不要加。\n"
        + "如果这句话不是让你操作电脑，或者用上面的动作做不到，输出 {\"steps\":[]}。\n"
        + "例：「打开记事本写今天要买牛奶」→ {\"steps\":[{\"do\":\"open\",\"target\":\"记事本\"},{\"do\":\"type\",\"text\":\"今天要买牛奶\"}]}\n"
        + "例：「声音开到三十」→ {\"steps\":[{\"do\":\"volume\",\"number\":30}]}\n"
        + "例：「讲个笑话」→ {\"steps\":[]}";

    // The model's answer → steps. Null: not usable (not JSON, an action outside the list, a value out of range) — then nothing
    // is done. An empty list: the model says it is not a command.
    internal static List<TaskStep>? ParsePlan(string answer)
    {
        int from = answer.IndexOf('{'), to = answer.LastIndexOf('}');
        if (from < 0 || to <= from) return null;
        try
        {
            using var document = JsonDocument.Parse(answer[from..(to + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var steps = new List<TaskStep>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || steps.Count >= MaxSteps) return null;
                string Field(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
                double number = item.TryGetProperty("number", out var n) && (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out double parsed)
                    || n.ValueKind == JsonValueKind.String && double.TryParse(n.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) ? parsed : double.NaN;
                if (PlanStep(Field("do").ToLowerInvariant(), Field("target"), Field("text"), number) is not { } step) return null;
                steps.Add(step);
            }
            return steps;
        }
        catch (JsonException) { return null; }
    }

    private static TaskStep? PlanStep(string action, string target, string text, double number)
    {
        switch (action)
        {
            case "open":
                if (target.Length is 0 or > 260 || target.Any(char.IsControl)) return null;
                if (target.Equals("browser", StringComparison.OrdinalIgnoreCase)) return new TaskStep(TaskKind.Open, "browser");
                if (target.Contains("://")) return NormalizeUrl(target) is { } url ? new TaskStep(TaskKind.Open, url) : null;
                return new TaskStep(TaskKind.Open, LooksLikeUrl(target) ? NormalizeUrl(target)! : NormalizePath(target) ?? target);
            case "search":
                return text.Length is > 0 and <= 200 ? new TaskStep(TaskKind.Search, Engine(target), text) : null;
            case "type":
                return text.Length is > 0 and <= MaxText ? new TaskStep(TaskKind.Type, Text: text) : null;
            case "key":
                return TryKeys(target, out string combo) ? new TaskStep(TaskKind.Key, combo) : null;
            case "volume":
                if (target.ToLowerInvariant() is "up" or "down" or "mute" or "unmute") return new TaskStep(TaskKind.Volume, target.ToLowerInvariant());
                return number is >= 0 and <= 100 ? new TaskStep(TaskKind.Volume, Number: Math.Round(number)) : null;
            case "window":
                return target.ToLowerInvariant() is "desktop" or "minimize" or "maximize" or "restore" or "close" or "switch" ? new TaskStep(TaskKind.Window, target.ToLowerInvariant()) : null;
            case "screenshot": return new TaskStep(TaskKind.Screenshot);
            case "lock": return new TaskStep(TaskKind.Lock);
            case "settings":
                return target == "pet" || SettingsUri(target) != null ? new TaskStep(TaskKind.Settings, target) : null;
            case "shutdown":
                return number is >= 0 and <= 1440 ? new TaskStep(TaskKind.Shutdown, Number: Math.Max(60, number * 60)) : null;
            case "cancel_shutdown": return new TaskStep(TaskKind.CancelShutdown);
            case "clip":
                return text.Length is > 0 and <= MaxText ? new TaskStep(TaskKind.Clip, Text: text) : null;
            case "wait":
                return number is >= 1 and <= 30 ? new TaskStep(TaskKind.Wait, Number: Math.Round(number)) : null;
            default:
                return null;
        }
    }

    // Programs she never types into: a command line would turn typed text into commands.
    internal static bool IsCommandLine(string processName) =>
        Regex.IsMatch(processName.Trim(), @"^(cmd|powershell|pwsh|windowsterminal|wt|conhost|openconsole|bash|wsl|wslhost|mintty|git-bash|ubuntu|debian|alacritty|wezterm-gui|tabby|putty|kitty)(\.exe)?$", RegexOptions.IgnoreCase);

    internal static readonly Regex Yes = new(@"^(?:执行|确定|确认|好的?|是的?|可以|做吧|行|嗯|ok|yes|开始)[。！!～~\s]*$", RegexOptions.IgnoreCase);
    internal static readonly Regex No = new(@"^(?:取消|不用了?|算了|不要|别做了?|不|no|停)[。！!～~\s]*$", RegexOptions.IgnoreCase);
}
