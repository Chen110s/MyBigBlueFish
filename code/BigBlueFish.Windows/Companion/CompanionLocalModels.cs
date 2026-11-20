using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BigBlueFish.Deployment;

namespace BigBlueFish.Windows;

// A local model that can be downloaded from the settings: a fixed file of a fixed revision, checked against its SHA-256.
internal sealed record LocalModelOffer(string Id, string Title, string FileName, string Url, string Sha256, long Size, string Note,
    int GoodVramMb, int GoodRamGb);

// One graphics device as llama-server lists it.
internal sealed record LocalGpu(string Name, long TotalMb, long FreeMb);

// 本地智能: which GGUF models are in data\models, what they are called, which bigger or smaller ones can be downloaded, and
// what this computer can carry.
internal static class CompanionLocalModels
{
    // Qwen3.5 small models (Apache-2.0), unsloth's GGUF builds on ModelScope (魔搭), pinned to the revision checked in
    // November 2026. The default 4B model is the one the official installer downloads.
    internal static readonly LocalModelOffer[] Offers =
    [
        new("qwen35-9b", "Qwen3.5-9B（更聪明）", "Qwen3.5-9B-Q4_K_M.gguf",
            "https://modelscope.cn/models/unsloth/Qwen3.5-9B-GGUF/resolve/ae90f0d1c1be2b9250b0ef68265615f6fe3c777b/Qwen3.5-9B-Q4_K_M.gguf",
            "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8", 5680522464,
            "比默认的 4B 聪明不少：更会接话、更能理解长一点的问题。显卡显存 8 GB 以上最顺；只用 CPU 时要 16 GB 以上内存，回答会慢很多。", 7000, 16),
        new("qwen35-2b", "Qwen3.5-2B（更快）", "Qwen3.5-2B-Q4_K_M.gguf",
            "https://modelscope.cn/models/unsloth/Qwen3.5-2B-GGUF/resolve/90057e31161eb95cc0bc1413c4f53b44de9b49c8/Qwen3.5-2B-Q4_K_M.gguf",
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223", 1280835840,
            "比默认的 4B 更快、更省内存，适合没有独立显卡、比较慢的电脑；但没那么聪明。", 2000, 8)
    ];

    internal static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";

    // A file name → what to call it.
    internal static string Label(string fileName)
    {
        if (ModelStore.Sources.Any(s => s.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            || fileName.Equals("Qwen3.5-4B-heretic-f16_Q4_K_M.gguf", StringComparison.OrdinalIgnoreCase))
            return "Qwen3.5-4B（默认）";
        if (Offers.FirstOrDefault(o => o.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)) is { } offer) return offer.Title;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    // The language models in data\models, smallest first. Vision projectors (mmproj) and unfinished downloads are left out.
    internal static List<(string FileName, long Size)> Installed(string modelsFolder)
    {
        var models = new List<(string, long)>();
        try
        {
            if (!Directory.Exists(modelsFolder)) return models;
            foreach (var file in new DirectoryInfo(modelsFolder).GetFiles("*.gguf"))
                if (!file.Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) && ModelStore.IsGguf(file.FullName)) models.Add((file.Name, file.Length));
        }
        catch { }
        return models.OrderBy(m => m.Item2).ToList();
    }

    // "  Vulkan0: NVIDIA GeForce RTX 4060 Laptop GPU (8188 MiB, 7524 MiB free)"
    private static readonly Regex Device = new(@"^[ \t]*[^:\r\n]+:[ \t]*(?<name>[^\r\n]+?)[ \t]*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB free\)", RegexOptions.Multiline);

    internal static List<LocalGpu> ParseDevices(string text) =>
        Device.Matches(text ?? "").Select(m => new LocalGpu(m.Groups["name"].Value.Trim(),
            long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture))).ToList();

    // Integrated graphics report the shared system memory as their own; it does not make a big model fast.
    internal static bool Integrated(string name) =>
        Regex.IsMatch(name, @"Intel\(R\) (UHD|HD|Iris)|Intel\(R\) Arc\(TM\) Graphics|Radeon\(TM\) (Graphics|Vega|\d{3}M)|Radeon \d{3}M|AMD Radeon Graphics|Microsoft Basic|llvmpipe", RegexOptions.IgnoreCase);

    internal static long BestVramMb(IEnumerable<LocalGpu> gpus) =>
        gpus.Where(g => !Integrated(g.Name)).Select(g => g.TotalMb).DefaultIfEmpty(0).Max();

    // Will a model run well here: on the graphics card, on the CPU with enough memory, or hardly at all.
    internal static string Fit(LocalModelOffer offer, long ramBytes, long vramMb)
    {
        double ramGb = ramBytes / 1073741824.0;
        if (vramMb >= offer.GoodVramMb) return "good";
        if (ramGb >= offer.GoodRamGb - 0.5) return "slow";
        return "tight";
    }

    internal static string Advice(long ramBytes, IReadOnlyList<LocalGpu> gpus)
    {
        long vram = BestVramMb(gpus);
        var nine = Offers[0];
        string fit = Fit(nine, ramBytes, vram);
        return fit switch
        {
            "good" => "建议：显卡够用，可以换成 Qwen3.5-9B，会聪明不少，速度也还行。",
            "slow" => "建议：没有够大的独立显卡，Qwen3.5-9B 能跑但每句要等比较久；想要快一点就留着 4B，嫌 4B 慢可以换 2B。",
            _ => "建议：这台电脑带 9B 比较吃力，留着默认的 4B；嫌慢可以换 Qwen3.5-2B。"
        };
    }

    internal static string Hardware(long ramBytes, IReadOnlyList<LocalGpu> gpus)
    {
        string memory = ramBytes > 0 ? "内存 " + (ramBytes / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB" : "内存大小没读到";
        if (gpus.Count == 0) return memory + "；没有找到能用来跑模型的显卡（只用 CPU）";
        return memory + "；" + string.Join("，", gpus.Select(g => g.Name + (Integrated(g.Name) ? "（集成显卡，共享内存）" : $"（显存 {g.TotalMb / 1024.0:0.#} GB）")));
    }
}

// The 模型与供电 page: which local model she thinks with, and downloads of the bigger and smaller ones.
public partial class MainWindow
{
    private sealed class ModelDownload
    {
        public required LocalModelOffer Offer;
        public long Received;
        public string Status = "准备下载…";
        public readonly CancellationTokenSource Cancel = new();
        public Task? Work;
        public bool Finished;
        public string? Error;
    }

    private ModelDownload? modelDownload;
    private HttpClient? modelWeb;
    private string? localHardware, localAdvice;
    private long localRam, localVram = -1;
    private Task? localHardwareTask;

    private static string ModelsFolder => Path.GetFullPath("models");

    private string LocalModelLabel() => brain.ModelPath is { } path ? CompanionLocalModels.Label(Path.GetFileName(path)) : "未安装";

    // Memory and graphics cards, read once (llama-server --list-devices takes a few seconds).
    private Task LoadLocalHardware()
    {
        if (localHardwareTask != null) return localHardwareTask;
        return localHardwareTask = Task.Run(async () =>
        {
            long ram = 0;
            try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { }
            string devices = "";
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                devices = await brain.ListDevices(limit.Token);
            }
            catch { }
            var gpus = CompanionLocalModels.ParseDevices(devices);
            localRam = ram;
            localVram = CompanionLocalModels.BestVramMb(gpus);
            localHardware = CompanionLocalModels.Hardware(ram, gpus);
            localAdvice = CompanionLocalModels.Advice(ram, gpus);
        });
    }

    // Thinks with another model from now on; a runner that has the old one loaded is replaced at once when it is idle,
    // otherwise before the next answer.
    private void UseLocalModel(string fileName)
    {
        var known = ModelStore.Sources.FirstOrDefault(s => s.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        var offer = CompanionLocalModels.Offers.FirstOrDefault(o => o.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        ModelStore.Save(Environment.CurrentDirectory, new ModelStore.Selection(known?.Id ?? "manual", fileName, known?.Sha256 ?? offer?.Sha256));
        brain.RefreshModelAvailability();
        if (!brain.Busy && brain.RunnerPid != null) brain.Stop();
        nextWarmup = DateTime.MinValue;
    }

    private void StartModelDownload(LocalModelOffer offer)
    {
        if (modelDownload is { Finished: false }) return;
        string final = Path.Combine(ModelsFolder, offer.FileName), partial = final + ".partial";
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        try
        {
            Directory.CreateDirectory(ModelsFolder);
            long free = new DriveInfo(Path.GetPathRoot(ModelsFolder)!).AvailableFreeSpace;
            if (free < offer.Size - have + (512L << 20))
            {
                ModernUI.Choice(companionSettings, "磁盘空间不够", $"下载 {offer.Title} 需要 {CompanionLocalModels.Size(offer.Size - have)}，{Path.GetPathRoot(ModelsFolder)} 只剩 {CompanionLocalModels.Size(free)}。", "知道了");
                return;
            }
        }
        catch { }
        if (localVram >= 0 && CompanionLocalModels.Fit(offer, localRam, localVram) == "tight"
            && ModernUI.Choice(companionSettings, "这台电脑可能带不动", $"{offer.Title} 需要 {offer.GoodVramMb / 1024.0:0} GB 以上显存，或 {offer.GoodRamGb} GB 以上内存；这台电脑是：{localHardware}。\n下载后可能很慢，甚至加载失败（可以随时换回默认模型）。仍要下载吗？",
                "仍要下载", "取消") != 0)
            return;
        var download = modelDownload = new ModelDownload { Offer = offer, Received = have };
        download.Work = Task.Run(() => DownloadModelAsync(download));
    }

    private async Task DownloadModelAsync(ModelDownload download)
    {
        var offer = download.Offer;
        var token = download.Cancel.Token;
        string final = Path.Combine(ModelsFolder, offer.FileName), partial = final + ".partial";
        modelWeb ??= new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20), AutomaticDecompression = DecompressionMethods.None })
            { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (have > offer.Size) { File.Delete(partial); have = 0; }
                download.Received = have;
                if (have == offer.Size) break;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, offer.Url);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BigBlueFish");
                    if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                    using var response = await modelWeb.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); continue; }
                    int code = (int)response.StatusCode;
                    if (code is >= 400 and < 500) throw new InvalidDataException($"魔搭社区拒绝了下载（HTTP {code}）");
                    response.EnsureSuccessStatusCode();
                    bool append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent; // 200: the server starts over
                    if (!append) have = 0;
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    var buffer = new byte[1 << 20];
                    download.Status = "正在下载";
                    while (true)
                    {
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                        stall.CancelAfter(TimeSpan.FromSeconds(60)); // nothing for a minute: the connection is dead
                        int read = await input.ReadAsync(buffer, stall.Token);
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        have += read;
                        download.Received = have;
                    }
                    attempt = 0;
                }
                catch (Exception error) when (!token.IsCancellationRequested && attempt < 30
                    && error is HttpRequestException or IOException or OperationCanceledException)
                {
                    download.Status = "网络断了，5 秒后接着下…";
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            download.Status = "下载完了，正在校验（几十秒）…";
            string hash = await ModelStore.Hash(partial, token);
            if (!hash.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("下载的文件和官方的对不上，已经删掉，请重新下载");
            }
            File.Move(partial, final, true);
            await Dispatcher.InvokeAsync(() => UseLocalModel(offer.FileName));
            download.Status = $"已下载并换成 {offer.Title}。第一次回答要先加载模型，稍等一会儿。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            download.Status = "已暂停下载（下载了的部分会保留，再点下载会接着下）。";
        }
        catch (Exception error)
        {
            download.Error = error.Message;
            download.Status = "下载没成功：" + error.Message;
        }
        finally { download.Finished = true; }
    }

    // Settings → 模型与供电, under the official model status.
    private void AddLocalModelSettings(StackPanel page, DispatcherTimer timer)
    {
        page.Children.Add(new TextBlock { Text = "本地智能：换模型", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4) });
        page.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            Text = "鱼鱼的本地智能是装在这台电脑上的语言模型：聊天（联网连不上或没填 Key 时）、看你在用的窗口说一句话、帮我做事里拆步骤都用它，内容不出这台电脑。"
                + "模型越大越聪明，也越吃显卡和内存。" });

        var current = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        current.Children.Add(new TextBlock { Text = "现在用的模型", Width = 92, VerticalAlignment = VerticalAlignment.Center });
        var choice = new ComboBox { MinWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        current.Children.Add(choice);
        page.Children.Add(current);
        var hardware = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 2), Text = "正在查看这台电脑的内存和显卡…" };
        var advice = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(80, 96, 152)) };
        page.Children.Add(hardware);
        page.Children.Add(advice);

        bool filling = false;
        string lastList = "";
        var offers = new List<(LocalModelOffer Offer, Button Button, TextBlock Note)>();
        foreach (var offer in CompanionLocalModels.Offers)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
            var button = ModernUI.Button("", () => { });
            button.HorizontalAlignment = HorizontalAlignment.Left;
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(button);
            row.Children.Add(note);
            page.Children.Add(row);
            offers.Add((offer, button, note));
            button.Click += (_, _) =>
            {
                if (modelDownload is { Finished: false } running)
                {
                    if (running.Offer == offer) running.Cancel.Cancel();
                    return;
                }
                if (CompanionLocalModels.Installed(ModelsFolder).Any(m => m.FileName.Equals(offer.FileName, StringComparison.OrdinalIgnoreCase)))
                    UseLocalModel(offer.FileName);
                else
                    StartModelDownload(offer);
                Refresh();
            };
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        var add = ModernUI.Button("从文件添加模型…", OpenModelInstaller);
        add.Margin = new Thickness(0, 0, 8, 0);
        tools.Children.Add(add);
        tools.Children.Add(ModernUI.Button("打开模型文件夹", () =>
        {
            try
            {
                Directory.CreateDirectory(ModelsFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModelsFolder}\"") { UseShellExecute = true });
            }
            catch { }
        }));
        page.Children.Add(tools);
        page.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 4, 0, 8),
            Text = "下载的是阿里通义千问 Qwen3.5 小模型（Apache-2.0 许可），来自魔搭社区 unsloth 的 GGUF 版本，下载后按 SHA-256 校验；断了再点会接着下。"
                + "换模型后第一次回答要先加载，大模型要等久一点。「从文件添加模型」可以放自己下载的 GGUF 模型（不保证稳定）。"
                + "用不着的模型可以在模型文件夹里删掉，省磁盘空间（正在用的那个别删）。" });

        choice.SelectionChanged += (_, _) =>
        {
            if (filling || choice.SelectedItem is not ComboBoxItem { Tag: string file }) return;
            if (brain.ModelPath is { } path && Path.GetFileName(path).Equals(file, StringComparison.OrdinalIgnoreCase)) return;
            UseLocalModel(file);
            Refresh();
        };

        void Refresh()
        {
            var installed = CompanionLocalModels.Installed(ModelsFolder);
            string using_ = brain.ModelPath is { } path ? Path.GetFileName(path) : "";
            string list = string.Join("|", installed.Select(m => m.FileName)) + ">" + using_;
            if (list != lastList)
            {
                lastList = list;
                filling = true;
                choice.Items.Clear();
                foreach (var (file, size) in installed)
                {
                    var item = new ComboBoxItem { Content = $"{CompanionLocalModels.Label(file)} · {CompanionLocalModels.Size(size)}", Tag = file };
                    choice.Items.Add(item);
                    if (file.Equals(using_, StringComparison.OrdinalIgnoreCase)) choice.SelectedItem = item;
                }
                if (installed.Count == 0) choice.Items.Add(new ComboBoxItem { Content = "还没有本地模型", IsEnabled = false });
                filling = false;
            }
            if (localHardware != null) { hardware.Text = "这台电脑：" + localHardware; advice.Text = localAdvice ?? ""; }
            foreach (var (offer, button, note) in offers)
            {
                bool have = installed.Any(m => m.FileName.Equals(offer.FileName, StringComparison.OrdinalIgnoreCase));
                bool inUse = offer.FileName.Equals(using_, StringComparison.OrdinalIgnoreCase);
                var job = modelDownload?.Offer == offer ? modelDownload : null;
                bool busy = modelDownload is { Finished: false };
                if (job is { Finished: false })
                {
                    button.Content = "暂停下载";
                    button.IsEnabled = true;
                    note.Text = $"{job.Status} {CompanionLocalModels.Size(job.Received)} / {CompanionLocalModels.Size(offer.Size)}（{job.Received * 100.0 / offer.Size:0}%）";
                    continue;
                }
                button.Content = inUse ? $"正在用 {offer.Title}" : have ? $"换成 {offer.Title}" : $"下载 {offer.Title}（{CompanionLocalModels.Size(offer.Size)}）";
                button.IsEnabled = !inUse && !busy;
                note.Text = (job?.Status is { } status ? status + "\n" : "") + offer.Note;
            }
        }

        timer.Tick += (_, _) => Refresh();
        Refresh();
        _ = LoadLocalHardware().ContinueWith(_ => Dispatcher.BeginInvoke(Refresh));
    }
}
