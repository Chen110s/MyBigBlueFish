using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

internal enum DeltaForceEvent { None, RaidStart, Extracted, Died, Downed, Victory, Defeat }

// Havoc: the frame belongs to Havoc Warfare (全面战场: victory / defeat / redeploy), whose deaths are not raids.
internal sealed record DeltaForceReading(DeltaForceEvent Event, string Map, long Haul, string BigText, int Words, bool Havoc = false);

// Reads one OCR'd frame of Delta Force (三角洲行动) and decides what is on screen. Only large text
// counts as an event, so the kill feed, task lists and teammates' small notices never trigger one.
internal static class DeltaForceScreen
{
    internal static readonly string[] Maps = ["零号大坝", "长弓溪谷", "巴克什", "航天基地", "潮汐监狱", "核电站"];
    private static readonly (string Key, string Map)[] EnglishMaps =
        [("ZERODAM", "零号大坝"), ("LAYALIGROVE", "长弓溪谷"), ("BRAKKESH", "巴克什"), ("SPACECITY", "航天基地"), ("TIDEPRISON", "潮汐监狱")];
    private static readonly string[] ExtractWords = ["撤离成功", "成功撤离", "EXTRACTIONSUCCESSFUL", "EXTRACTIONSUCCESS", "SUCCESSFULLYEXTRACTED", "EXTRACTED"];
    private static readonly string[] DeathWords = ["撤离失败", "行动失败", "你已阵亡", "已阵亡", "阵亡", "失踪", "KILLEDINACTION", "MISSINGINACTION", "EXTRACTIONFAILED"];
    private static readonly string[] DeathTokens = ["KIA", "MIA"];
    private static readonly string[] DownedWords = ["你已被击倒", "被击倒", "已倒地", "DOWNED"];
    private static readonly string[] RedeployWords = ["重新部署", "选择部署点", "等待部署", "复活", "REDEPLOY", "RESPAWN"];
    // Screens that show a map name without being a fresh deployment: lobby, map picker, settlement.
    private static readonly string[] NotDeployWords =
        ["开始行动", "开始游戏", "开始匹配", "取消匹配", "匹配中", "交易行", "特勤处", "仓库", "选择地图", "地图选择", "进入游戏",
         "结算", "带出", "收益", "获得经验", "返回大厅", "本局", "价值", "战绩"];
    // In priority order: the value carried out beats any other income line on the same screen.
    private static readonly string[] HaulLabels = ["带出总价值", "带出价值", "撤离价值", "总带出", "带出收益", "本局总收益", "本局收益"];
    private static readonly Regex Amount = new(@"([-−]?)(\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)(万|W|M|K)?", RegexOptions.CultureInvariant);

    private sealed class Segment
    {
        internal readonly List<CompanionOcrWord> Words = new();
        internal string Text = "";
        internal double Height, Left, Right, Top, Bottom;
        internal double CenterX => (Left + Right) / 2;
        internal double CenterY => (Top + Bottom) / 2;
    }

    internal static string Normalize(string text)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).Select(c => c switch
        {
            >= '０' and <= '９' => (char)(c - '０' + '0'),
            '，' => ',',
            '．' => '.',
            _ => char.ToUpperInvariant(c)
        });
        return new string(chars.ToArray());
    }

    internal static DeltaForceReading Analyze(IReadOnlyList<CompanionOcrWord> words, int width, int height)
    {
        if (words.Count == 0 || width <= 0 || height <= 0) return new(DeltaForceEvent.None, "", 0, "", 0);
        var segments = Segments(words);
        double big = height * .026, medium = height * .02;
        string bigText = string.Join(" | ", segments.Where(s => s.Height >= medium).OrderBy(s => s.Top).Select(s => s.Text));
        // Words that rule out a deployment are read from UI-sized text only, not the tiny HUD labels.
        string uiText = string.Concat(segments.Where(s => s.Height >= height * .014).Select(s => s.Text));
        bool redeploy = Contains(uiText, RedeployWords);
        long haul = FindHaul(segments);

        var maps = segments.Where(s => s.Height >= medium).Select(MapIn).Where(m => m.Length > 0).Distinct().ToList();
        string map = maps.Count == 1 ? maps[0] : "";

        var banners = segments.Where(s => s.Height >= big && s.CenterX > width * .04 && s.CenterX < width * .96
            && s.CenterY > height * .03 && s.CenterY < height * .94).ToList();
        int extracted = banners.Count(s => Contains(s.Text, ExtractWords));
        int died = banners.Count(s => !Contains(s.Text, ExtractWords) && (Contains(s.Text, DeathWords) || HasToken(s, DeathTokens)));
        // One outcome banner is a result; several at once is a list of past raids.
        if (extracted is 1 or 2 && died == 0) return new(DeltaForceEvent.Extracted, map, haul, bigText, words.Count);
        if (died is 1 or 2 && extracted == 0)
            return redeploy ? new(DeltaForceEvent.None, "", 0, bigText, words.Count, true) : new(DeltaForceEvent.Died, map, haul, bigText, words.Count);
        if (extracted + died > 0) return new(DeltaForceEvent.None, "", 0, bigText, words.Count);

        if (banners.Any(s => Contains(s.Text, DownedWords))) return new(DeltaForceEvent.Downed, map, 0, bigText, words.Count);
        // Havoc Warfare (全面战场) rounds end with a short victory / defeat banner.
        if (banners.Any(s => s.Text.Length <= 8 && (s.Text.Contains("胜利") || s.Text.Contains("VICTORY"))))
            return new(DeltaForceEvent.Victory, "", 0, bigText, words.Count, true);
        if (banners.Any(s => s.Text.Length <= 8 && (s.Text is "失败" or "战败" || s.Text.Contains("DEFEAT"))))
            return new(DeltaForceEvent.Defeat, "", 0, bigText, words.Count, true);

        if (map.Length > 0 && !redeploy && !NotDeployWords.Any(uiText.Contains))
            return new(DeltaForceEvent.RaidStart, map, haul, bigText, words.Count);
        return new(DeltaForceEvent.None, map, haul, bigText, words.Count, redeploy);
    }

    private static List<Segment> Segments(IReadOnlyList<CompanionOcrWord> words)
    {
        // Rows by vertical centre and similar height, then split each row at wide horizontal gaps.
        var rows = new List<List<CompanionOcrWord>>();
        foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Height > 0).OrderBy(w => w.CenterY).ThenBy(w => w.X))
        {
            var row = rows.LastOrDefault(r =>
            {
                double h = r.Average(w => w.Height);
                return Math.Abs(r.Average(w => w.CenterY) - word.CenterY) <= Math.Min(h, word.Height) * .5
                    && word.Height <= h * 1.8 && word.Height * 1.8 >= h;
            });
            if (row == null) rows.Add(new List<CompanionOcrWord> { word });
            else row.Add(word);
        }
        var segments = new List<Segment>();
        foreach (var row in rows)
        {
            Segment? current = null;
            foreach (var word in row.OrderBy(w => w.X))
            {
                if (current == null || word.X - current.Right > Math.Max(current.Words[^1].Height, word.Height) * 2.5)
                {
                    current = new Segment { Left = word.X, Right = word.Right, Top = word.Y, Bottom = word.Y + word.Height };
                    segments.Add(current);
                }
                current.Words.Add(word);
                current.Right = Math.Max(current.Right, word.Right);
                current.Top = Math.Min(current.Top, word.Y);
                current.Bottom = Math.Max(current.Bottom, word.Y + word.Height);
            }
        }
        foreach (var segment in segments)
        {
            var heights = segment.Words.Select(w => w.Height).OrderBy(h => h).ToArray();
            segment.Height = heights[heights.Length / 2];
            segment.Text = Normalize(string.Concat(segment.Words.Select(w => w.Text)));
        }
        return segments;
    }

    // Exact match, or one misread character for keywords of four characters or more.
    internal static bool Contains(string text, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal)) return true;
            if (keyword.Length < 4 || text.Length < keyword.Length || keyword.Any(c => c < 128)) continue;
            for (int start = 0; start + keyword.Length <= text.Length; start++)
            {
                int misses = 0;
                for (int i = 0; i < keyword.Length && misses <= 1; i++)
                    if (text[start + i] != keyword[i]) misses++;
                if (misses <= 1) return true;
            }
        }
        return false;
    }

    private static bool HasToken(Segment segment, string[] tokens) =>
        segment.Words.Any(w => tokens.Contains(Normalize(w.Text))) || tokens.Contains(segment.Text);

    private static string MapIn(Segment segment)
    {
        foreach (var map in Maps)
            if (Contains(segment.Text, [map])) return map;
        foreach (var (key, map) in EnglishMaps)
            if (segment.Text.Contains(key, StringComparison.Ordinal)) return map;
        return "";
    }

    private static long FindHaul(List<Segment> segments)
    {
        foreach (var key in HaulLabels)
        foreach (var label in segments.Where(s => s.Text.Contains(key, StringComparison.Ordinal)))
        {
            long value = ParseAmount(label.Text[(label.Text.IndexOf(key, StringComparison.Ordinal) + key.Length)..]);
            if (value > 0) return value;
            if (value < 0) continue; // a loss: this label has no haul, and a number nearby belongs to something else
            // The number may sit to the right on the same line or just below the label.
            var neighbour = segments.Where(s => s != label && (
                    Math.Abs(s.CenterY - label.CenterY) <= label.Height && s.Left >= label.Right - 2 && s.Left - label.Right < label.Height * 12 ||
                    s.Top >= label.CenterY && s.Top - label.Bottom < label.Height * 2.5 && s.Left < label.Right && s.Right > label.Left))
                .OrderBy(s => Math.Abs(s.CenterY - label.CenterY) + Math.Abs(s.Left - label.Right) * .2);
            foreach (var candidate in neighbour)
            {
                value = ParseAmount(candidate.Text);
                if (value > 0) return value;
                if (value < 0) break;
            }
        }
        return 0;
    }

    // The first plausible amount in the text (0: none); -1 for a negative number, a loss, which is never a haul.
    internal static long ParseAmount(string text)
    {
        foreach (Match match in Amount.Matches(Normalize(text)))
        {
            if (!double.TryParse(match.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            value *= match.Groups[3].Value switch { "万" or "W" => 10_000, "M" => 1_000_000, "K" => 1_000, _ => 1 };
            if (match.Groups[1].Value.Length > 0) return -1;
            if (value >= 1_000d && value <= 5e9) return (long)Math.Round(value);
        }
        return 0;
    }
}
