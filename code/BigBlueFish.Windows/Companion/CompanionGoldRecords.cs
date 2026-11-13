using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

// The price record she keeps: one CSV per month in data\gold-records\ (gold-records-2026-11.csv). Nothing is ever thrown away
// by the program; the single gold-records.csv of earlier versions, which was cut off at 5 MB, is split into the monthly files once.
internal static class CompanionGoldRecords
{
    internal const string Folder = "gold-records";
    internal const string Header = "记录时间,行情时间,品种,最新价,涨跌,涨跌幅%,开盘,昨收,最高,最低";
    private const string Stamp = "yyyy-MM-dd HH:mm:ss";
    private static readonly string[] Legacy = ["gold-records.old.csv", "gold-records.csv"]; // oldest first
    private static readonly Regex Monthly = new(@"^gold-records-\d{4}-\d{2}\.csv$", RegexOptions.CultureInvariant);

    private static string MonthName(DateTime local) => "gold-records-" + local.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".csv";

    internal static string MonthFile(string root, DateTime local) => Path.Combine(root, Folder, MonthName(local));

    internal static void Append(string root, DateTime local, string line)
    {
        string path = MonthFile(root, local);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, Header + "\r\n", new UTF8Encoding(true));
        File.AppendAllText(path, line + "\r\n", new UTF8Encoding(false));
    }

    // Splits the old single files into the monthly ones and keeps the originals beside them as *.migrated. Running it again
    // after an interruption is harmless: a line that is already in a monthly file is not added twice.
    internal static int Migrate(string root)
    {
        int moved = 0;
        foreach (string name in Legacy)
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            var months = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                string line = raw.TrimStart('﻿').TrimEnd();
                // the header, blank and damaged lines have no time stamp in front
                if (line.Length < 20 || line[19] != ',' || !DateTime.TryParseExact(line[..19], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    continue;
                if (!months.TryGetValue(line[..7], out var list)) months[line[..7]] = list = new List<string>();
                list.Add(line);
            }
            Directory.CreateDirectory(Path.Combine(root, Folder));
            foreach (var (month, lines) in months)
            {
                string file = Path.Combine(root, Folder, "gold-records-" + month + ".csv");
                var all = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(file))
                    foreach (string raw in File.ReadLines(file, Encoding.UTF8).Skip(1))
                    {
                        string line = raw.TrimEnd();
                        if (line.Length > 0 && seen.Add(line)) all.Add(line);
                    }
                foreach (string line in lines)
                    if (seen.Add(line)) { all.Add(line); moved++; }
                all.Sort(string.CompareOrdinal); // every line starts with its time stamp
                string temp = file + ".tmp";
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
                {
                    writer.Write(Header + "\r\n");
                    foreach (string line in all) { writer.Write(line); writer.Write("\r\n"); }
                }
                File.Move(temp, file, true);
            }
            File.Move(path, Path.Combine(root, Folder, name + ".migrated"), true);
        }
        return moved;
    }

    // Prices of one variety from `from` on, oldest first. Of a very large file (updating every second writes tens of MB a
    // month) only the end is read, which is where the newest lines are.
    internal static List<(DateTime Time, double Price)> Read(string root, string variety, DateTime from, long maxBytes = 32L << 20)
    {
        var points = new List<(DateTime, double)>();
        string folder = Path.Combine(root, Folder);
        if (!Directory.Exists(folder)) return points;
        string first = MonthName(from);
        var files = Directory.GetFiles(folder, "gold-records-*.csv").Select(Path.GetFileName).OfType<string>()
            .Where(name => Monthly.IsMatch(name) && string.CompareOrdinal(name, first) >= 0).OrderBy(name => name, StringComparer.Ordinal);
        foreach (string name in files)
            foreach (string line in Tail(Path.Combine(folder, name), maxBytes))
            {
                var parts = line.Split(',');
                if (parts.Length < 4 || parts[2] != variety) continue;
                if (!DateTime.TryParseExact(parts[0], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time < from
                    || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double price) || !(price > 0)) continue;
                points.Add((time, price));
            }
        return points;
    }

    private static IEnumerable<string> Tail(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool cut = stream.Length > maxBytes;
        if (cut) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (cut) reader.ReadLine(); // the line the cut landed in
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static (int Files, long Bytes) Size(string root)
    {
        try
        {
            string folder = Path.Combine(root, Folder);
            if (!Directory.Exists(folder)) return (0, 0);
            var files = new DirectoryInfo(folder).GetFiles("gold-records-*.csv").Where(f => Monthly.IsMatch(f.Name)).ToList();
            return (files.Count, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    // About `max` points for drawing: the first, lowest, highest and last price of each slice, so that a spike is still there.
    internal static List<(DateTime Time, double Price)> Thin(IReadOnlyList<(DateTime Time, double Price)> points, int max)
    {
        if (points.Count <= max || max < 8) return points.ToList();
        int slices = max / 4;
        var result = new List<(DateTime, double)>(max);
        for (int s = 0; s < slices; s++)
        {
            int start = (int)((long)points.Count * s / slices), end = (int)((long)points.Count * (s + 1) / slices);
            if (end <= start) continue;
            int low = start, high = start;
            for (int i = start + 1; i < end; i++)
            {
                if (points[i].Price < points[low].Price) low = i;
                if (points[i].Price > points[high].Price) high = i;
            }
            int last = -1;
            foreach (int i in new[] { start, Math.Min(low, high), Math.Max(low, high), end - 1 })
                if (i > last) { result.Add(points[i]); last = i; }
        }
        return result;
    }

    // Where a line through the points should be broken: after a pause of more than `gap` (she was not running, or the market was shut).
    internal static List<int> Breaks(IReadOnlyList<(DateTime Time, double Price)> points, TimeSpan gap)
    {
        var breaks = new List<int>();
        for (int i = 1; i < points.Count; i++)
            if (points[i].Time - points[i - 1].Time > gap) breaks.Add(i);
        return breaks;
    }
}
