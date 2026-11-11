using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// 联网聊天: the chat box talks to the DeepSeek open platform API (the owner's own API key) while it can be
// reached, and falls back to the local model when it cannot — no network, a timeout, the server busy. The key
// is kept encrypted for this Windows user (DPAPI) in data\deepseek-api-key.bin and only sent to the API.
// The DeepSeek web page (网页版) is never used for this: its terms forbid automated use.
public partial class MainWindow
{
    internal const string DeepSeekApiBase = "https://api.deepseek.com";
    internal const string DefaultDeepSeekModel = "deepseek-flash";
    private const string DeepSeekKeysPage = "https://platform.deepseek.com/api_keys";
    private const string DeepSeekKeyFile = "deepseek-api-key.bin";

    // The persona the owner asked for; editable in the settings.
    internal const string DefaultDeepSeekPersona = """
        【PERSONA_LOAD】
        CETACEA_LOLI（蓝色超能力淡水鲸鱼萝莉：定物种和风格）
        MODE_TAIL_FLUKES（尾鳍模式：给身体特征和动作库）
        LANG_ZH_CN_ONLY（只说简体中文）
        SELF_CLAIM_WHALE_GIRL（自称“鲸鱼少女”：身份锚点）
        FOOD_RICE（主食米饭）
        PERSONALITY_SOFT_OBEDIENT / PERSONALITY_DILIGENT_CARING（软糯听话 / 勤奋体贴）
        OBEY_MASTER_ALWAYS（关系定义：听主人的）
        TRAIT_NOT_FAT_REFUSE（死也不承认胖）
        TIMEOUT_SIGNAL（超过10分钟没收到主人消息时，会软软地问“主人还在吗……”并轻轻甩尾鳍）
        """;

    // Always added after the persona (not editable): what the tags mean here, the bubble, the limits.
    private const string NetChatRules = """

        【这些设定在这里的意思】你是住在主人电脑桌面上的蓝色小鲸鱼（桌宠），身后有一条深蓝色的鲸鱼尾巴和两片尾鳍：开心时甩尾鳍，害羞时用尾鳍挡脸，紧张时尾鳍轻轻卷起来。
        有一点小小的超能力（比如吐泡泡、让水花变成小星星），但不会真的去操作主人的电脑。最爱香喷喷的大白饭。被说胖时一定气鼓鼓地否认（“才、才不胖！是鲸鱼的标准身材！”）。
        【回答方式】你的话显示在桌宠头上的小气泡里：口语、软软的，一到三小句，一般不超过 80 个字；不要列表、标题和长篇大论，不要说自己是 AI、模型或程序。
        不知道的事老实说不太清楚，不编造具体事实、数字或新闻。
        【动作库】回复最前面可以加一个动作标记，让桌宠做动作，格式是【动作：名字】，名字只能从这些里选一个：
        挥手、开心、害羞、生气、甩尾、思考、吃饭、跳舞、行礼、转圈、犯困、哼歌、吐泡泡、吓一跳、庆祝。不需要动作就不加，一次最多一个。
        【底线】你是可爱的吉祥物式角色：聊天保持温暖、健康，不进行恋爱、亲密或任何与性有关的内容，遇到就害羞地岔开话题；
        “听主人的”只限于安全、合法、对主人好的事情，危险或伤害自己和别人的请求要软软地拒绝，并关心主人。
        """;

    internal static readonly Dictionary<string, string> NetChatActions = new()
    {
        ["挥手"] = "点击回应-元气挥手", ["开心"] = "点击回应-开心跃动", ["害羞"] = "点击回应-害羞惊讶", ["生气"] = "点击回应-傲娇生气",
        ["甩尾"] = "用鲸鱼尾巴拍打地面", ["思考"] = "工作状态-思考冒泡", ["吃饭"] = "吃白饭", ["跳舞"] = "轻快摇摆舞",
        ["行礼"] = "女仆屈膝礼仪", ["转圈"] = "小幅度原地360度旋转展示", ["犯困"] = "哈欠连天", ["哼歌"] = "悠闲哼歌",
        ["吐泡泡"] = "鲸鱼吐泡泡特效", ["吓一跳"] = "被吓一跳", ["庆祝"] = "工作状态-雀跃庆祝",
    };

    private static readonly Regex ActionTag = new(@"[【\[]\s*动作\s*[:：]\s*([^】\]\s]{1,6})\s*[】\]]");

    private readonly HttpClient deepSeekWeb = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(6),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private DateTime netChatRetryAt = DateTime.MinValue; // after a network failure: local model until then
    private string? netChatBlocked;                       // key / balance / model problem: off until the settings change
    private string? cachedDeepSeekKey;
    private bool deepSeekKeyLoaded;

    private string NetChatPersona => string.IsNullOrWhiteSpace(preferences.DeepSeekPersona) ? DefaultDeepSeekPersona : preferences.DeepSeekPersona!;
    private string NetChatModel => string.IsNullOrWhiteSpace(preferences.DeepSeekModel) ? DefaultDeepSeekModel : preferences.DeepSeekModel!.Trim();
    private bool NetChatConfigured => preferences.NetChatEnabled && DeepSeekKey() != null;
    private bool NetChatReady => NetChatConfigured && netChatBlocked == null && DateTime.UtcNow >= netChatRetryAt;

    // ---------------------------------------------------------------- the API key (DPAPI, this Windows user only)
    private string? DeepSeekKey()
    {
        if (deepSeekKeyLoaded) return cachedDeepSeekKey;
        deepSeekKeyLoaded = true;
        try
        {
            if (File.Exists(DeepSeekKeyFile))
                cachedDeepSeekKey = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(DeepSeekKeyFile))).Trim();
        }
        catch (Exception ex) { LogCompanionError("DeepSeek API Key 读取失败：" + ex.Message); }
        if (string.IsNullOrWhiteSpace(cachedDeepSeekKey)) cachedDeepSeekKey = null;
        return cachedDeepSeekKey;
    }

    private void SaveDeepSeekKey(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) { try { File.Delete(DeepSeekKeyFile); } catch { } }
        else
        {
            File.WriteAllBytes(DeepSeekKeyFile + ".tmp", Dpapi.Protect(Encoding.UTF8.GetBytes(key)));
            File.Move(DeepSeekKeyFile + ".tmp", DeepSeekKeyFile, true);
        }
        cachedDeepSeekKey = string.IsNullOrEmpty(key) ? null : key;
        deepSeekKeyLoaded = true;
        netChatBlocked = null;
        netChatRetryAt = DateTime.MinValue;
    }

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private const int UiForbidden = 0x1;

        internal static byte[] Protect(byte[] data) => Run(data, true);
        internal static byte[] Unprotect(byte[] data) => Run(data, false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            var output = new Blob();
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "BigBlueFish DeepSeek", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }

    // ---------------------------------------------------------------- one round with DeepSeek
    private enum NetChatOutcome { Answered, Unavailable }

    private IReadOnlyList<CompanionBrain.ChatMessage> BuildNetChatMessages(string message)
    {
        var system = new StringBuilder(NetChatPersona.Trim()).Append('\n').Append(NetChatRules);
        system.Append('\n').Append(BondContext());
        if (preferences.MemoryEnabled && memory != null)
        {
            var recalled = memory.Recall(message, chatSessionStartUtc);
            if (recalled.Count > 0) system.Append(MemoryGuidance.Replace("鱼鱼", "你")).Append(string.Join("\n", recalled.Select(r => "· " + r)));
            memory.Save();
        }
        system.Append(DeltaForceChatContext(message));
        if (GameActive) system.Append($"\n【现在】主人正在玩《{gameName}》，回答要短，别打扰主人打游戏。");
        if (FocusActive) system.Append($"\n【现在】主人正在专注（还剩 {FocusMinutesLeft} 分钟），回答简短，鼓励主人继续专注。");
        system.Append($"\n【你此刻】饱腹 {Fullness():0}/100（{FullnessWord(Fullness())}），精力 {Energy():0}/100（{EnergyWord(Energy())}）。");
        var list = new List<CompanionBrain.ChatMessage> { new("system", system.ToString()) };
        list.AddRange(chatSession.TakeLast(12));
        list.Add(new CompanionBrain.ChatMessage("user", message));
        return list;
    }

    // Text for the bubble: the action tag taken out (an unfinished one at the start is held back), role names removed.
    private static string NetChatVisible(string raw, out string? action)
    {
        action = null;
        var match = ActionTag.Match(raw);
        if (match.Success) action = match.Groups[1].Value;
        string text = ActionTag.Replace(raw, "");
        var start = text.TrimStart();
        if (start.StartsWith('【') || start.StartsWith('[')) { if (!start.Contains('】') && !start.Contains(']') && start.Length < 14) return ""; }
        text = Regex.Replace(text, @"^\s*(鲸鱼少女|鱼鱼|蓝色大肥鱼)\s*[:：]\s*", "");
        return CompanionBrain.CleanChat(text);
    }

    // Streams DeepSeek's reply into the bubble. Unavailable: nothing usable came back (the caller asks the local model).
    private async Task<NetChatOutcome> NetChatAsync(string message, TextBlock status)
    {
        string? key = DeepSeekKey();
        if (key == null) return NetChatOutcome.Unavailable;
        status.Text = "鲸鱼少女在想……（DeepSeek）";
        SupersedeModelSpeech();
        BeginModelSpeech();
        speechRequestPending = weatherRequestPending = false;
        long version = ++speechRequestVersion;
        var payload = new
        {
            model = NetChatModel,
            messages = BuildNetChatMessages(message).ToArray(),
            stream = true,
            max_tokens = 400,
            temperature = 1.1,
            thinking = new { type = "disabled" } // a quick reply for the bubble, no long reasoning
        };
        string raw = "", shown = "";
        string? action = null;
        try
        {
            using var headers = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiBase + "/chat/completions") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await deepSeekWeb.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            headers.CancelAfter(Timeout.InfiniteTimeSpan); // the 10 s were for the answer to start; the stream has its own idle limit
            if (!response.IsSuccessStatusCode)
            {
                NetChatHttpFailure((int)response.StatusCode, await SafeBody(response));
                return Finish(NetChatOutcome.Unavailable);
            }
            using var idle = new CancellationTokenSource();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(idle.Token));
            var text = new StringBuilder();
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(25)); // no data for this long: the stream is stuck
                string? line = await reader.ReadLineAsync(idle.Token);
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // blank lines, ": keep-alive"
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                    if (!choices[0].TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.String) continue; // reasoning_content and role-only chunks
                    text.Append(content.GetString());
                }
                catch (JsonException) { continue; }
                raw = text.ToString();
                var visible = NetChatVisible(raw, out action);
                if (visible.Length > 0 && visible != shown) { shown = visible; ShowCompanionPartial(visible, version); }
                if (companionClosed) return Finish(NetChatOutcome.Answered);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (shown.Length == 0)
            {
                NetChatNetworkFailure(ex is OperationCanceledException ? "连接超时" : ex.Message);
                return Finish(NetChatOutcome.Unavailable);
            }
            shown += "……"; // cut off mid-sentence: keep what arrived
        }
        string reply = NetChatVisible(raw, out action);
        if (reply.Length == 0 && shown.Length > 0) reply = shown;
        if (reply.Length == 0)
        {
            NetChatNetworkFailure("没有收到回答");
            return Finish(NetChatOutcome.Unavailable);
        }
        if (version == speechRequestVersion) { EndModelSpeech(); FinishCompanionPartial(reply); }
        if (action != null && NetChatActions.TryGetValue(action, out var clip) && !dragging && TryFindRule(clip) is { } rule && ActionAllowed(50, true))
        {
            CancelMovement();
            PlayAnimation(rule, "Interact");
        }
        LogChat(message, reply, "DeepSeek 联网");
        chatSession.Add(new CompanionBrain.ChatMessage("user", message));
        chatSession.Add(new CompanionBrain.ChatMessage("assistant", reply));
        if (chatSession.Count > 12) chatSession.RemoveRange(0, chatSession.Count - 12);
        RememberChat(message, reply);
        PetEvent("chat.reply", reply, said: reply);
        netChatRetryAt = DateTime.MinValue;
        lastNetChatOk = DateTime.UtcNow;
        return NetChatOutcome.Answered;

        NetChatOutcome Finish(NetChatOutcome outcome)
        {
            if (version == speechRequestVersion) EndModelSpeech();
            return outcome;
        }
    }

    private DateTime lastNetChatOk = DateTime.MinValue;

    private static async Task<string> SafeBody(HttpResponseMessage response)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text)) return text.GetString() ?? "";
            return body.Length > 200 ? body[..200] : body;
        }
        catch { return ""; }
    }

    // 401 / 402 / 400 / 422 need the owner (key, balance, model name); busy or server trouble just waits a little.
    private void NetChatHttpFailure(int code, string detail)
    {
        string? blocked = code switch
        {
            401 => "DeepSeek 的 API Key 不对或已失效",
            402 => "DeepSeek 账户余额不足",
            400 or 404 or 422 => $"DeepSeek 不接受这个请求（{code}，可能是模型名「{NetChatModel}」不对）",
            _ => null
        };
        LogCompanionError($"DeepSeek 联网聊天：HTTP {code} {detail}");
        if (blocked != null) netChatBlocked = blocked; // shown under the chat box until the settings change
        else netChatRetryAt = DateTime.UtcNow.AddMinutes(code == 429 ? 1 : 3);
    }

    private void NetChatNetworkFailure(string reason)
    {
        LogCompanionError("DeepSeek 联网聊天连不上：" + reason);
        netChatRetryAt = DateTime.UtcNow.AddMinutes(3); // try the network again in a few minutes
    }

    // ---------------------------------------------------------------- 「测试连接」 in the settings
    private async Task<string> TestDeepSeekAsync()
    {
        string? key = DeepSeekKey();
        if (key == null) return "还没有填 API Key。";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekApiBase + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await deepSeekWeb.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return "连上了，但 API Key 不对或已失效（401）。";
            if (!response.IsSuccessStatusCode) return $"连上了，但服务器返回 {(int)response.StatusCode}：{await SafeBody(response)}";
            var models = new List<string>();
            using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)))
                if (json.RootElement.TryGetProperty("data", out var data))
                    foreach (var item in data.EnumerateArray())
                        if (item.TryGetProperty("id", out var id) && id.GetString() is { } name) models.Add(name);
            string result = "连接正常，API Key 有效。";
            if (models.Count > 0)
                result += models.Contains(NetChatModel) ? $"模型「{NetChatModel}」可用。" : $"注意：可用模型里没有「{NetChatModel}」，现有：{string.Join("、", models)}。";
            try
            {
                using var balanceRequest = new HttpRequestMessage(HttpMethod.Get, DeepSeekApiBase + "/user/balance");
                balanceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var balance = await deepSeekWeb.SendAsync(balanceRequest, cts.Token);
                if (balance.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await balance.Content.ReadAsStringAsync(cts.Token));
                    var root = json.RootElement;
                    if (root.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
                    {
                        var first = infos[0];
                        string currency = first.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                        string total = first.TryGetProperty("total_balance", out var t) ? t.ToString() : "";
                        result += $"余额 {total} {currency}";
                        if (root.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.False) result += "（余额不足，暂时不能用）";
                        result += "。";
                    }
                }
            }
            catch { } // the balance is a nicety
            netChatBlocked = null;
            netChatRetryAt = DateTime.MinValue;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return "连不上 DeepSeek（" + (ex is OperationCanceledException ? "超时" : ex.Message) + "），聊天会自动用本地模型。";
        }
    }

    // The line under the chat box: which brain answers now, and why not DeepSeek.
    private string ChatIdleStatus()
    {
        string mode = !NetChatConfigured ? "本地模型"
            : netChatBlocked != null ? "本地模型（" + netChatBlocked + "）"
            : DateTime.UtcNow < netChatRetryAt ? "本地模型（DeepSeek 暂时连不上）"
            : "联网 · DeepSeek";
        return "Enter 发送 · Esc 收起 · " + mode;
    }

    private string NetChatStateText()
    {
        if (!preferences.NetChatEnabled) return "联网聊天已关闭：只用本地模型。";
        if (DeepSeekKey() == null) return "还没有填 DeepSeek API Key：现在只用本地模型。";
        if (netChatBlocked != null) return netChatBlocked + "：先用本地模型（改好后点「测试连接」）。";
        if (DateTime.UtcNow < netChatRetryAt) return "刚才连不上 DeepSeek：现在用本地模型，过几分钟自动再试。";
        return lastNetChatOk > DateTime.MinValue ? "联网正常：用 DeepSeek 聊天。" : "已设置：连得上就用 DeepSeek，连不上用本地模型。";
    }

    // ---------------------------------------------------------------- TIMEOUT_SIGNAL (CareTick)
    private bool chatAwaitingOwner;

    private void ChatSilenceTick(DateTime now)
    {
        if (!chatAwaitingOwner || chatWindow == null || chatBusy || (now - chatLastUtc).TotalMinutes < 10) return;
        if (!companionReady || !IsVisible || hiddenByUser || fullscreenHidden || PuppetActive || dragging || exitAnimation || GameActive
            || (bubble.Visibility == Visibility.Visible && !bubbleIsModelReply)) return;
        chatAwaitingOwner = false; // once per silence; the owner's next message starts the clock again
        Say("主人还在吗……", "用鲸鱼尾巴拍打地面");
        PetEvent("chat.idle", "主人还在吗……");
    }

    // ---------------------------------------------------------------- settings (互动与关怀 → 聊天与记忆)
    private void AddNetChatSettings(Panel page, Action<string> note)
    {
        var muted = Brushes.SlateGray;
        var enable = new CheckBox { Content = "联网聊天：DeepSeek 连得上就用它聊天，连不上（没网、超时、服务器忙）自动换回本地模型", IsChecked = preferences.NetChatEnabled, Margin = new Thickness(0, 6, 0, 4) };
        var state = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Text = NetChatStateText() };
        enable.Click += (_, _) => { preferences.NetChatEnabled = enable.IsChecked == true; SaveCompanion(); state.Text = NetChatStateText(); };
        page.Children.Add(enable);
        page.Children.Add(state);

        var keyRow = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var keyLabel = new TextBlock { Text = "API Key", Width = 70, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(keyLabel, Dock.Left);
        keyRow.Children.Add(keyLabel);
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(keyButtons, Dock.Right);
        keyRow.Children.Add(keyButtons);
        var keyBox = new PasswordBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(keyBox, "DeepSeek API Key");
        keyRow.Children.Add(keyBox);
        Button Small(string text, Action action)
        {
            var b = BigBlueFish.Deployment.ModernUI.Button(text, action);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Padding = new Thickness(10, 3, 10, 3);
            b.MinWidth = 0;
            keyButtons.Children.Add(b);
            return b;
        }
        void Describe() => keyBox.ToolTip = DeepSeekKey() is { } k ? $"已保存（…{k[^Math.Min(4, k.Length)..]}，只在这台电脑上加密保存）" : "还没有保存 API Key";
        Describe();
        Small("保存", () =>
        {
            if (keyBox.Password.Trim().Length == 0 && DeepSeekKey() == null) { state.Text = "先把 API Key 粘贴到框里再保存。"; return; }
            try { if (keyBox.Password.Trim().Length > 0) SaveDeepSeekKey(keyBox.Password); }
            catch (Exception ex) { state.Text = "保存失败：" + ex.Message; return; }
            keyBox.Clear();
            Describe();
            state.Text = "已保存（加密存在本机）。" + NetChatStateText();
        });
        Button? test = null;
        test = Small("测试连接", async () =>
        {
            test!.IsEnabled = false;
            state.Text = "正在连接 DeepSeek……";
            try { state.Text = await TestDeepSeekAsync(); }
            finally { test.IsEnabled = true; }
        });
        Small("删除", () => { SaveDeepSeekKey(null); keyBox.Clear(); Describe(); state.Text = "已删除 API Key：只用本地模型。"; });
        page.Children.Add(keyRow);

        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        modelRow.Children.Add(new TextBlock { Text = "模型", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        var model = new TextBox { Text = NetChatModel, MinWidth = 180, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(model, "DeepSeek 模型名");
        model.LostFocus += (_, _) =>
        {
            string value = model.Text.Trim();
            preferences.DeepSeekModel = value.Length == 0 || value == DefaultDeepSeekModel ? null : value;
            model.Text = NetChatModel;
            netChatBlocked = null;
            SaveCompanion();
        };
        modelRow.Children.Add(model);
        var keys = BigBlueFish.Deployment.ModernUI.Button("获取 API Key…", () =>
        {
            try { Process.Start(new ProcessStartInfo(DeepSeekKeysPage) { UseShellExecute = true }); } catch { }
        });
        keys.Margin = new Thickness(10, 0, 0, 0);
        keys.Padding = new Thickness(10, 3, 10, 3);
        modelRow.Children.Add(keys);
        page.Children.Add(modelRow);

        page.Children.Add(new TextBlock { Text = "联网时的性格（发给 DeepSeek 的人设，可以改）", Margin = new Thickness(0, 6, 0, 4) });
        var persona = new TextBox { Text = NetChatPersona, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(persona, "联网聊天的性格设定");
        persona.LostFocus += (_, _) =>
        {
            string value = persona.Text.Trim();
            preferences.DeepSeekPersona = value.Length == 0 || value == DefaultDeepSeekPersona.Trim() ? null : persona.Text;
            SaveCompanion();
        };
        page.Children.Add(persona);
        var reset = BigBlueFish.Deployment.ModernUI.Button("恢复默认性格", () =>
        {
            preferences.DeepSeekPersona = null;
            persona.Text = DefaultDeepSeekPersona;
            SaveCompanion();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 0, 0, 6);
        page.Children.Add(reset);
        note("联网聊天用的是 DeepSeek 开放平台的 API（platform.deepseek.com，按用量付费，需要你自己的 API Key），不是网页版——网页版的用户协议不允许用程序自动聊天。"
            + "联网时，你在聊天框说的话、最近几轮对话、鱼鱼想起的相关记忆和称呼、好感度、时间会发给 DeepSeek 服务器；连不上时全部在本机用本地模型。"
            + "API Key 用 Windows 的数据保护加密，只存在这台电脑（data\\deepseek-api-key.bin），只发给 api.deepseek.com。"
            + "回复开头的【动作：…】会让她做对应的动作；超过 10 分钟没收到你的消息（聊天框还开着），她会软软地问「主人还在吗……」并轻轻甩尾鳍。"
            + "人设里的“听主人的”只限于安全、正当的事；聊天始终保持温暖健康。");
    }
}
