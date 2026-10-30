using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// skin.json inside an appearance pack (all optional).
internal sealed class CompanionSkinInfo
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    // What plays for an action the pack does not have: "idle" (the pack's own idle clip) or "fish" (the original).
    public string? Missing { get; set; }
}

internal sealed record CompanionSkin(string Folder, string Name, string? Author, bool UseIdleForMissing,
    IReadOnlyDictionary<string, string> Files, IReadOnlyList<CompanionAnimationRule> Extra)
{
    internal int Covered(IEnumerable<CompanionAnimationRule> original) => original.Count(r => Files.ContainsKey(r.Id));
}

// Appearance packs (形象包): folders under <app>\skins\. Each holds clips named after the actions
// (e.g. 待机呼吸休闲.webm) and optionally animations.json / skin.json. The original fish stays the base:
// every rule, weight and schedule is kept, a pack only swaps the clips it provides.
internal static class CompanionSkins
{
    internal const string IdleId = "待机呼吸休闲";
    internal static readonly string[] Extensions = [".webm", ".gif", ".apng", ".mov", ".mkv", ".png"];
    internal static string Root => Path.Combine(AppContext.BaseDirectory, "skins");

    internal static IEnumerable<string> Folders() =>
        Directory.Exists(Root) ? Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.CurrentCulture) : Enumerable.Empty<string>();

    internal static CompanionSkin? Load(string folder, IReadOnlyList<CompanionAnimationRule> original)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var info = new CompanionSkinInfo();
            var infoFile = Path.Combine(folder, "skin.json");
            if (File.Exists(infoFile)) info = JsonSerializer.Deserialize<CompanionSkinInfo>(File.ReadAllText(infoFile)) ?? info;
            var known = original.Select(r => r.Id).ToHashSet();
            var files = new Dictionary<string, string>();
            var extra = new List<CompanionAnimationRule>();
            string? Clip(string? name)
            {
                if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) return null;
                var path = Path.Combine(folder, name);
                return File.Exists(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ? path : null;
            }
            // 1. animations.json in the pack: replacements for existing actions, or new actions of its own.
            var listFile = Path.Combine(folder, "animations.json");
            if (File.Exists(listFile))
            {
                foreach (var entry in JsonSerializer.Deserialize<List<CompanionAnimationRule>>(File.ReadAllText(listFile)) ?? new())
                {
                    if (string.IsNullOrWhiteSpace(entry.Id) || Clip(entry.File) is not { } path) continue;
                    if (known.Contains(entry.Id)) { files[entry.Id] = path; continue; }
                    if (extra.Any(r => r.Id == entry.Id)) continue;
                    entry.File = path;
                    if (entry.Pool is not ("Event" or "Idle" or "Click" or "Disabled")) entry.Pool = "Event";
                    entry.MoveKind = null; // a pack's own clips always play in place
                    extra.Add(entry);
                }
            }
            // 2. Clips named after an action (待机呼吸休闲.webm, 吃白饭.gif …) or after the original file.
            foreach (var rule in original)
            {
                if (files.ContainsKey(rule.Id)) continue;
                var path = Clip(rule.File) ?? Extensions.Select(e => Clip(rule.Id + e)).FirstOrDefault(p => p != null);
                if (path != null) files[rule.Id] = path;
            }
            if (files.Count == 0 && extra.Count == 0) return null;
            bool idle = files.ContainsKey(IdleId) && !string.Equals(info.Missing, "fish", StringComparison.OrdinalIgnoreCase);
            string name = string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileName(folder) : info.Name!.Trim();
            return new CompanionSkin(folder, name.Length > 24 ? name[..24] : name, info.Author, idle, files, extra);
        }
        catch { return null; } // a broken pack is simply not offered
    }

    private static CompanionAnimationRule Copy(CompanionAnimationRule rule) =>
        JsonSerializer.Deserialize<CompanionAnimationRule>(JsonSerializer.Serialize(rule))!;

    internal static CompanionAnimationPolicy Policy(IReadOnlyList<CompanionAnimationRule> original, CompanionSkin skin)
    {
        // Pools in which the pack has a clip that can be picked at any time: there, missing actions are left out
        // of the random choice (a pool must never end up empty, so seasonal or night-only clips do not count).
        static bool AlwaysEligible(CompanionAnimationRule r) => r.Weight > 0 && r.Festival == null && r.SeasonOnly == null && !r.NightOnly;
        var provided = original.Where(r => skin.Files.ContainsKey(r.Id) && AlwaysEligible(r)).Select(r => r.Pool)
            .Concat(skin.Extra.Where(AlwaysEligible).Select(r => r.Pool)).ToHashSet();
        var rules = new List<CompanionAnimationRule>();
        foreach (var rule in original)
        {
            var copy = Copy(rule);
            if (skin.Files.TryGetValue(rule.Id, out var path))
            {
                copy.File = path;
                copy.MoveKind = null; // walking events are timed to the original clips
            }
            else if (skin.UseIdleForMissing)
            {
                copy.File = skin.Files[IdleId];
                copy.MoveKind = null;
                if (provided.Contains(rule.Pool) && rule.Pool != "Disabled") copy.Pool = "Disabled";
            }
            rules.Add(copy);
        }
        rules.AddRange(skin.Extra);
        return new CompanionAnimationPolicy(rules);
    }
}

public partial class MainWindow
{
    private CompanionAnimationPolicy? originalAnimationPolicy;
    private CompanionSkin? activeSkin;

    // True when a rule's clip comes from an appearance pack (its File is then a full path).
    private static bool IsSkinClip(CompanionAnimationRule rule) => Path.IsPathRooted(rule.File);

    // Switches to the pack in `folder` ("" = the original fish). Returns false when the pack cannot be used.
    private bool ApplySkin(string? folder, bool replay)
    {
        if (originalAnimationPolicy == null) return false;
        CompanionSkin? skin = null;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var path = Path.IsPathRooted(folder) ? folder : Path.Combine(CompanionSkins.Root, folder);
            skin = CompanionSkins.Load(path, originalAnimationPolicy.Rules);
        }
        activeSkin = skin;
        animationPolicy = skin == null ? originalAnimationPolicy : CompanionSkins.Policy(originalAnimationPolicy.Rules, skin);
        preferences.Skin = skin == null ? "" : Path.GetFileName(skin.Folder);
        if (replay && companionReady && !companionClosed && !exitAnimation && !PuppetActive && !dragging)
        {
            if (Crawling) StopCrawlNow(false);
            CancelMovement();
            PlayIdle();
        }
        return skin != null || string.IsNullOrWhiteSpace(folder);
    }

    // A clip of the pack failed to play: go back to the original look rather than stop animating.
    private bool RecoverFromSkinFailure()
    {
        if (activeSkin == null) return false;
        string name = activeSkin.Name;
        ApplySkin("", false);
        SaveCompanion();
        if (companionReady && !exitAnimation) PlayIdle();
        ShowCompanionNotice($"形象包「{name}」有动作播放失败，已换回蓝色大肥鱼。可以检查一下素材格式哦～");
        return true;
    }

    private void AddSkinSettings(Panel page)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = "形象", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        var combo = new ComboBox { MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(combo, "形象");
        row.Children.Add(combo);
        var status = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        var folders = new List<string>();
        bool filling = false;
        void Refresh()
        {
            filling = true;
            combo.Items.Clear();
            folders.Clear();
            combo.Items.Add("蓝色大肥鱼（原版）");
            folders.Add("");
            int total = originalAnimationPolicy?.Rules.Count ?? 106;
            foreach (var folder in CompanionSkins.Folders())
            {
                if (originalAnimationPolicy == null || CompanionSkins.Load(folder, originalAnimationPolicy.Rules) is not { } skin) continue;
                int covered = skin.Covered(originalAnimationPolicy.Rules);
                combo.Items.Add($"{skin.Name}（{covered}/{total} 个动作" + (skin.Extra.Count > 0 ? $"，另有 {skin.Extra.Count} 个新动作" : "") + "）");
                folders.Add(Path.GetFileName(folder));
            }
            int index = folders.FindIndex(f => string.Equals(f, preferences.Skin, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : 0;
            status.Text = folders.Count == 1
                ? "还没有形象包。点「打开形象包文件夹」，按里面的说明放入动画素材后点「重新扫描」。"
                : activeSkin == null ? "正在使用原版形象。" : StatusOf(activeSkin);
            filling = false;
        }
        string StatusOf(CompanionSkin skin)
        {
            int total = originalAnimationPolicy?.Rules.Count ?? 106;
            int covered = skin.Covered(originalAnimationPolicy?.Rules ?? Array.Empty<CompanionAnimationRule>());
            return $"正在使用「{skin.Name}」" + (string.IsNullOrWhiteSpace(skin.Author) ? "" : $"（作者：{skin.Author}）") + $"：{covered}/{total} 个动作由形象包提供，"
                + (covered == total ? "完整替换。" : skin.UseIdleForMissing ? "其余动作用它的待机动作代替。" : "其余动作沿用原版鱼鱼。");
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (filling || combo.SelectedIndex < 0 || combo.SelectedIndex >= folders.Count) return;
            if (!ApplySkin(folders[combo.SelectedIndex], true))
            {
                status.Text = "这个形象包读取失败，已保持原版形象。";
                return;
            }
            SaveCompanion();
            status.Text = activeSkin == null ? "正在使用原版形象。" : StatusOf(activeSkin);
        };
        page.Children.Add(row);
        page.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        var open = BigBlueFish.Deployment.ModernUI.Button("打开形象包文件夹", () =>
        {
            try
            {
                Directory.CreateDirectory(CompanionSkins.Root);
                WriteSkinGuide();
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + CompanionSkins.Root + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { status.Text = "无法打开形象包文件夹：" + ex.Message; }
        });
        open.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(open);
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button("重新扫描", Refresh));
        page.Children.Add(buttons);
        page.Children.Add(new TextBlock { Text = "形象包是 skins 文件夹里的一个子文件夹，放入以动作命名的透明背景动画（如「待机呼吸休闲.webm」），缺少的动作会用它的待机动作代替。详见文件夹里的「形象包说明.txt」和「动作清单.txt」。",
            FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Refresh();
    }

    // Writes the format guide and the list of action names next to the packs (only if missing).
    private void WriteSkinGuide()
    {
        var guide = Path.Combine(CompanionSkins.Root, "形象包说明.txt");
        if (!File.Exists(guide))
            File.WriteAllText(guide, """
                蓝色大肥鱼 · 形象包说明
                ========================

                每个形象包是这个 skins 文件夹里的一个子文件夹，例如：
                  skins\我的形象\
                      skin.json            （可选）名称、作者等
                      待机呼吸休闲.webm     最重要：平时的待机动作
                      女仆屈膝礼仪.webm     启动时的问候
                      被鼠标拖拽悬空反馈.webm 被拖动时
                      点击回应-元气挥手.webm …… 其余动作按「动作清单.txt」里的名字命名

                素材要求
                  · 透明背景。推荐 VP9 编码的 WebM（带透明通道），也支持 GIF、APNG、MOV（带透明通道）、
                    以及静态 PNG（当作 3 秒的动作）。
                  · 推荐 640×360、24 帧/秒，人物居中、脚踩画面底边；其他尺寸会等比缩放后贴底居中。
                  · 每个动作建议 3～10 秒。

                缺少的动作
                  · 默认用这个形象包的「待机呼吸休闲」代替，随机动作只会从形象包提供的动作里挑；
                  · 想让缺少的动作沿用原版鱼鱼，在 skin.json 里写 "Missing": "fish"。

                skin.json 示例（UTF-8 编码）：
                  { "Name": "我的形象", "Author": "我", "Missing": "idle" }

                新增动作（可选）：在形象包里放一个 animations.json，格式与程序目录 assets\fish\animations.json 相同，
                  写入原版没有的 Id 和文件名，就会作为新的随机动作（Pool 可为 Event / Idle / Click）。

                放好后：设置 → 陪伴与外观 → 形象 →「重新扫描」，选择即可立即切换；选「蓝色大肥鱼（原版）」可随时换回。
                如果某个动作播放失败，会自动换回原版并提示。
                注意：游戏里模仿开枪时的枪口火光、摸头的位置等是按原版鱼鱼的身形设计的，换形象后可能对不齐。
                请只使用你有权使用的素材。
                """, new UTF8Encoding(true));
        var list = Path.Combine(CompanionSkins.Root, "动作清单.txt");
        if (!File.Exists(list) && originalAnimationPolicy != null)
        {
            var text = new StringBuilder("动作清单（文件名 = 动作名 + 扩展名，例如 待机呼吸休闲.webm）\r\n\r\n");
            string PoolName(string pool) => pool switch
            {
                "Idle" => "待机", "Event" => "随机动作", "Click" => "点击回应", "Greeting" => "启动问候", "Drag" => "被拖动",
                "Daily" => "每日", "Disabled" => "由程序在特定场合播放", _ => pool
            };
            foreach (var group in originalAnimationPolicy.Rules.GroupBy(r => r.Pool))
            {
                text.Append('【').Append(PoolName(group.Key)).Append("】\r\n");
                foreach (var rule in group) text.Append("  ").Append(rule.Id).Append("\r\n");
                text.Append("\r\n");
            }
            File.WriteAllText(list, text.ToString(), new UTF8Encoding(true));
        }
    }
}
