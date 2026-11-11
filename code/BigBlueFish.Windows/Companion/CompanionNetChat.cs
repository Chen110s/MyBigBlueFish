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

// 联网聊天: while a network model can be reached the chat box talks to it, otherwise (no network, a timeout, the
// server busy, the free quota used up) the local model answers. Two providers: OpenRouter's free models (a free
// account and key; DeepSeek's free model first when a provider serves it, then other free models) or the paid
// DeepSeek open platform API. Keys are kept DPAPI-encrypted for this Windows user in the data folder and only sent
// to that provider. The DeepSeek web page (网页版) is never used for this: its terms forbid automated use.
public partial class MainWindow
{
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

    // ---------------------------------------------------------------- providers
    // OpenRouter: a free account and key, free models (":free", 50 requests a day, 20 a minute); the list is tried
    // in order, a model with no provider serving it right now is skipped for a while.
    // DeepSeek: the official open platform API, paid by use.
    private sealed record NetProvider(string Id, string Title, string Short, string BaseUrl, string KeyFile, string KeysPage, string DefaultModels);

    private static readonly NetProvider OpenRouterProvider = new("openrouter", "OpenRouter 免费模型（免费 Key，每天约 50 次）", "OpenRouter",
        "https://openrouter.ai/api/v1", "openrouter-api-key.bin", "https://openrouter.ai/settings/keys",
        "deepseek/deepseek-v4-flash:free, google/gemma-4-31b-it:free, nvidia/nemotron-3-ultra-550b-a55b:free");
    private static readonly NetProvider DeepSeekProvider = new("deepseek", "DeepSeek 官方 API（付费 Key）", "DeepSeek",
        "https://api.deepseek.com", "deepseek-api-key.bin", "https://platform.deepseek.com/api_keys", "deepseek-flash");
    private static readonly NetProvider[] NetProviders = [OpenRouterProvider, DeepSeekProvider];

    private NetProvider CurrentProvider => preferences.NetChatProvider == DeepSeekProvider.Id ? DeepSeekProvider : OpenRouterProvider;

    private string NetChatModelText(NetProvider provider) =>
        (provider == DeepSeekProvider ? preferences.DeepSeekModel : preferences.OpenRouterModels) is { } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim() : provider.DefaultModels;

    private IReadOnlyList<string> NetChatModels(NetProvider provider) =>
        NetChatModelText(provider).Split(new[] { ',', '，', ';', '；', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().Take(provider == DeepSeekProvider ? 1 : 6).ToList();

    private readonly HttpClient netChatWeb = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(6),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private DateTime netChatRetryAt = DateTime.MinValue; // network down / quota used up: local model until then
    private string? netChatRetryWhy;
    private string? netChatBlocked;                       // key / account problem: off until the settings change
    private string? lastNetChatModel;
    private readonly Dictionary<string, DateTime> netModelSkipUntil = new(); // OpenRouter models that just failed
    private readonly Dictionary<string, string?> netKeys = new();

    private string NetChatPersona => string.IsNullOrWhiteSpace(preferences.DeepSeekPersona) ? DefaultDeepSeekPersona : preferences.DeepSeekPersona!;
    private bool NetChatConfigured => preferences.NetChatEnabled && NetKey(CurrentProvider) != null;
    private bool NetChatReady => NetChatConfigured && netChatBlocked == null && DateTime.UtcNow >= netChatRetryAt;

    private void ResetNetChatState()
    {
        netChatBlocked = null;
        netChatRetryAt = DateTime.MinValue;
        netChatRetryWhy = null;
        netModelSkipUntil.Clear();
    }

    // ---------------------------------------------------------------- the keys (DPAPI, this Windows user only)
    private string? NetKey(NetProvider provider)
    {
        if (netKeys.TryGetValue(provider.Id, out var cached)) return cached;
        string? key = null;
        try
        {
            if (File.Exists(provider.KeyFile)) key = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(provider.KeyFile))).Trim();
        }
        catch (Exception ex) { LogCompanionError($"{provider.Short} Key 读取失败：" + ex.Message); }
        if (string.IsNullOrWhiteSpace(key)) key = null;
        netKeys[provider.Id] = key;
        return key;
    }

    private void SaveNetKey(NetProvider provider, string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) { try { File.Delete(provider.KeyFile); } catch { } }
        else
        {
            File.WriteAllBytes(provider.KeyFile + ".tmp", Dpapi.Protect(Encoding.UTF8.GetBytes(key)));
            File.Move(provider.KeyFile + ".tmp", provider.KeyFile, true);
        }
        netKeys[provider.Id] = string.IsNullOrEmpty(key) ? null : key;
        ResetNetChatState();
    }

    private HttpRequestMessage NetRequest(NetProvider provider, HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, provider.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (provider == OpenRouterProvider)
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Chen110s/MyBigBlueFish");
            request.Headers.TryAddWithoutValidation("X-Title", "BigBlueFish Desktop Pet");
        }
        return request;
    }

    // ---------------------------------------------------------------- one round
    private enum NetChatOutcome { Answered, Unavailable }
    private enum Attempt { Answered, NextModel, Stop }

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

    // Streams the reply into the bubble, trying the provider's models in order. Unavailable: nothing usable came back
    // (the caller asks the local model).
    private async Task<NetChatOutcome> NetChatAsync(string message, TextBlock status)
    {
        var provider = CurrentProvider;
        string? key = NetKey(provider);
        if (key == null) return NetChatOutcome.Unavailable;
        status.Text = $"鲸鱼少女在想……（{provider.Short}）";
        SupersedeModelSpeech();
        BeginModelSpeech();
        speechRequestPending = weatherRequestPending = false;
        long version = ++speechRequestVersion;
        var messages = BuildNetChatMessages(message).ToArray();
        bool tried = false;
        foreach (var model in NetChatModels(provider))
        {
            if (netModelSkipUntil.TryGetValue(model, out var until) && DateTime.UtcNow < until) continue;
            tried = true;
            var (attempt, reply, action) = await NetChatAttemptAsync(provider, key, model, messages, version);
            if (attempt == Attempt.Answered)
            {
                if (version == speechRequestVersion) { EndModelSpeech(); FinishCompanionPartial(reply); }
                if (action != null && NetChatActions.TryGetValue(action, out var clip) && !dragging && TryFindRule(clip) is { } rule && ActionAllowed(50, true))
                {
                    CancelMovement();
                    PlayAnimation(rule, "Interact");
                }
                LogChat(message, reply, $"{provider.Short} 联网（{model}）");
                chatSession.Add(new CompanionBrain.ChatMessage("user", message));
                chatSession.Add(new CompanionBrain.ChatMessage("assistant", reply));
                if (chatSession.Count > 12) chatSession.RemoveRange(0, chatSession.Count - 12);
                RememberChat(message, reply);
                PetEvent("chat.reply", reply, said: reply);
                lastNetChatModel = model;
                netChatRetryAt = DateTime.MinValue;
                netChatRetryWhy = null;
                return NetChatOutcome.Answered;
            }
            if (attempt == Attempt.Stop || companionClosed) break;
        }
        if (version == speechRequestVersion) EndModelSpeech();
        if (netChatBlocked == null && DateTime.UtcNow >= netChatRetryAt)
        {
            // every model failed (or all are resting after a failure): local model for a few minutes
            netChatRetryAt = DateTime.UtcNow.AddMinutes(3);
            netChatRetryWhy = tried ? "联网模型暂时都不可用" : "联网模型刚才都失败了，稍后再试";
        }
        return NetChatOutcome.Unavailable;
    }

    private async Task<(Attempt Attempt, string Reply, string? Action)> NetChatAttemptAsync(NetProvider provider, string key, string model,
        CompanionBrain.ChatMessage[] messages, long version)
    {
        object payload = provider == DeepSeekProvider
            ? new { model, messages, stream = true, max_tokens = 400, temperature = 1.1, thinking = new { type = "disabled" } }
            : new { model, messages, stream = true, max_tokens = 400, temperature = 0.9, reasoning = new { effort = "none" } };
        string raw = "", shown = "";
        string? action = null;
        try
        {
            using var headers = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var request = NetRequest(provider, HttpMethod.Post, "/chat/completions", key);
            request.Content = JsonContent.Create(payload);
            using var response = await netChatWeb.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            headers.CancelAfter(Timeout.InfiniteTimeSpan); // the 12 s were for the answer to start; the stream has its own idle limit
            if (!response.IsSuccessStatusCode)
                return (NetChatHttpFailure(provider, model, (int)response.StatusCode, await SafeBody(response), response), "", null);
            using var idle = new CancellationTokenSource();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(idle.Token));
            var text = new StringBuilder();
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(25)); // no data for this long: the stream is stuck
                string? line = await reader.ReadLineAsync(idle.Token);
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // blank lines, ": OPENROUTER PROCESSING"
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    var root = chunk.RootElement;
                    if (root.TryGetProperty("error", out var error)) // an error after the stream started (HTTP 200)
                    {
                        if (shown.Length > 0) break;
                        int code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 502;
                        string why = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        return (NetChatHttpFailure(provider, model, code, why, null), "", null);
                    }
                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                    if (!choices[0].TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.String) continue; // reasoning and role-only chunks
                    text.Append(content.GetString());
                }
                catch (JsonException) { continue; }
                raw = text.ToString();
                var visible = NetChatVisible(raw, out action);
                if (visible.Length > 0 && visible != shown) { shown = visible; ShowCompanionPartial(visible, version); }
                if (companionClosed) return (Attempt.Stop, "", null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (shown.Length == 0)
            {
                LogCompanionError($"{provider.Short} 联网聊天连不上（{model}）：" + (ex is OperationCanceledException ? "超时" : ex.Message));
                netChatRetryAt = DateTime.UtcNow.AddMinutes(3); // the network itself: no use trying the other models
                netChatRetryWhy = "连不上" + provider.Short;
                return (Attempt.Stop, "", null);
            }
            shown += "……"; // cut off mid-sentence: keep what arrived
        }
        string reply = NetChatVisible(raw, out action);
        if (reply.Length == 0 && shown.Length > 0) reply = shown;
        if (reply.Length == 0)
        {
            LogCompanionError($"{provider.Short} 联网聊天：{model} 没有回答内容");
            netModelSkipUntil[model] = DateTime.UtcNow.AddMinutes(10);
            return (provider == DeepSeekProvider ? Attempt.Stop : Attempt.NextModel, "", null);
        }
        return (Attempt.Answered, reply, action);
    }

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

    // What a failed request means: the key or account needs the owner (stop until the settings change), the free
    // daily quota is used up (stop until it resets), or just this model is unavailable (try the next one).
    private Attempt NetChatHttpFailure(NetProvider provider, string model, int code, string detail, HttpResponseMessage? response)
    {
        LogCompanionError($"{provider.Short} 联网聊天：{model} HTTP {code} {detail}");
        if (code == 401) { netChatBlocked = $"{provider.Short} 的 Key 不对或已失效"; return Attempt.Stop; }
        if (code == 402) { netChatBlocked = provider == DeepSeekProvider ? "DeepSeek 账户余额不足" : "OpenRouter 账户欠费（余额为负时免费模型也用不了）"; return Attempt.Stop; }
        if (provider == DeepSeekProvider)
        {
            if (code is 400 or 404 or 422) { netChatBlocked = $"DeepSeek 不接受这个请求（{code}，可能是模型名「{model}」不对）"; return Attempt.Stop; }
            netChatRetryAt = DateTime.UtcNow.AddMinutes(code == 429 ? 1 : 3);
            netChatRetryWhy = code == 429 ? "DeepSeek 限流" : "DeepSeek 服务器忙";
            return Attempt.Stop;
        }
        if (detail.Contains("data policy", StringComparison.OrdinalIgnoreCase) || detail.Contains("privacy", StringComparison.OrdinalIgnoreCase))
        {
            netChatBlocked = "OpenRouter 的隐私设置不允许免费模型（到 openrouter.ai/settings/privacy 打开免费模型）";
            return Attempt.Stop;
        }
        if (code == 429 && response != null && response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
        {
            // the free-model quota is shared by every ":free" model: wait for its reset (the next UTC day for the daily one)
            DateTime reset = DateTime.UtcNow.AddMinutes(1);
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out long ms))
                reset = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            netChatRetryAt = reset < DateTime.UtcNow ? DateTime.UtcNow.AddMinutes(1) : reset.AddSeconds(5);
            netChatRetryWhy = (netChatRetryAt - DateTime.UtcNow).TotalMinutes > 5 ? "今天的免费次数用完了" : "免费模型每分钟次数用完了";
            return Attempt.Stop;
        }
        // this model only: no provider serving it now (404), rejected settings (400), busy (429/5xx)
        netModelSkipUntil[model] = DateTime.UtcNow.AddMinutes(code is 404 or 400 ? 30 : 5);
        return Attempt.NextModel;
    }

    // ---------------------------------------------------------------- 「测试连接」 in the settings
    private async Task<string> TestNetChatAsync()
    {
        var provider = CurrentProvider;
        string? key = NetKey(provider);
        if (key == null) return "还没有填 Key。";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string result;
            if (provider == OpenRouterProvider)
            {
                using (var request = NetRequest(provider, HttpMethod.Get, "/key", key))
                using (var response = await netChatWeb.SendAsync(request, cts.Token))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return "连上了，但 OpenRouter 的 Key 不对或已失效（401）。";
                    if (!response.IsSuccessStatusCode) return $"连上了，但 OpenRouter 返回 {(int)response.StatusCode}：{await SafeBody(response)}";
                    result = "连接正常，Key 有效。";
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                    if (json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("free_model_daily_requests", out var free)
                        && free.ValueKind == JsonValueKind.Object)
                    {
                        string Num(string name) => free.TryGetProperty(name, out var v) ? v.ToString() : "?";
                        result += $"今天免费次数：已用 {Num("used")} / {Num("limit")}。";
                    }
                }
                var states = new List<string>();
                foreach (var model in NetChatModels(provider))
                {
                    try
                    {
                        using var request = NetRequest(provider, HttpMethod.Get, "/models/" + model + "/endpoints", key);
                        using var response = await netChatWeb.SendAsync(request, cts.Token);
                        int count = -1;
                        if (response.IsSuccessStatusCode)
                        {
                            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                            if (json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("endpoints", out var endpoints)) count = endpoints.GetArrayLength();
                        }
                        states.Add($"{model}：" + (count > 0 ? "可用" : count == 0 ? "现在没有服务商提供（会自动跳过）" : $"查不到（{(int)response.StatusCode}）"));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException) { states.Add($"{model}：查不到"); }
                }
                result += "\n" + string.Join("\n", states);
            }
            else
            {
                using var request = NetRequest(provider, HttpMethod.Get, "/models", key);
                using var response = await netChatWeb.SendAsync(request, cts.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return "连上了，但 DeepSeek 的 API Key 不对或已失效（401）。";
                if (!response.IsSuccessStatusCode) return $"连上了，但 DeepSeek 返回 {(int)response.StatusCode}：{await SafeBody(response)}";
                var models = new List<string>();
                using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)))
                    if (json.RootElement.TryGetProperty("data", out var data))
                        foreach (var item in data.EnumerateArray())
                            if (item.TryGetProperty("id", out var id) && id.GetString() is { } name) models.Add(name);
                string model = NetChatModels(provider).FirstOrDefault() ?? DeepSeekProvider.DefaultModels;
                result = "连接正常，API Key 有效。";
                if (models.Count > 0) result += models.Contains(model) ? $"模型「{model}」可用。" : $"注意：可用模型里没有「{model}」，现有：{string.Join("、", models)}。";
                try
                {
                    using var balanceRequest = NetRequest(provider, HttpMethod.Get, "/user/balance", key);
                    using var balance = await netChatWeb.SendAsync(balanceRequest, cts.Token);
                    if (balance.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await balance.Content.ReadAsStringAsync(cts.Token));
                        if (json.RootElement.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
                        {
                            var first = infos[0];
                            result += $"余额 {(first.TryGetProperty("total_balance", out var t) ? t.ToString() : "?")} {(first.TryGetProperty("currency", out var c) ? c.GetString() : "")}。";
                        }
                    }
                }
                catch { } // the balance is a nicety
            }
            ResetNetChatState();
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return $"连不上 {provider.Short}（" + (ex is OperationCanceledException ? "超时" : ex.Message) + "），聊天会自动用本地模型。";
        }
    }

    // The line under the chat box: which brain answers now, and why not the network.
    private string ChatIdleStatus()
    {
        string mode = !NetChatConfigured ? "本地模型"
            : netChatBlocked != null ? "本地模型（" + netChatBlocked + "）"
            : DateTime.UtcNow < netChatRetryAt ? "本地模型（" + (netChatRetryWhy ?? "联网暂时不可用") + "）"
            : "联网 · " + CurrentProvider.Short + (lastNetChatModel is { } m ? "（" + m[(m.IndexOf('/') + 1)..] + "）" : "");
        return "Enter 发送 · Esc 收起 · " + mode;
    }

    private string NetChatStateText()
    {
        if (!preferences.NetChatEnabled) return "联网聊天已关闭：只用本地模型。";
        var provider = CurrentProvider;
        if (NetKey(provider) == null) return $"还没有填 {provider.Short} 的 Key：现在只用本地模型。";
        if (netChatBlocked != null) return netChatBlocked + "：先用本地模型（改好后点「测试连接」）。";
        if (DateTime.UtcNow < netChatRetryAt) return (netChatRetryWhy ?? "联网暂时不可用") + $"：现在用本地模型，{netChatRetryAt.ToLocalTime():HH:mm} 后自动再试。";
        return lastNetChatModel != null ? $"联网正常：用 {provider.Short}（{lastNetChatModel}）聊天。" : $"已设置：连得上就用 {provider.Short}，连不上用本地模型。";
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
        var enable = new CheckBox { Content = "联网聊天：连得上就用联网模型聊天，连不上（没网、超时、服务器忙、免费次数用完）自动换回本地模型", IsChecked = preferences.NetChatEnabled, Margin = new Thickness(0, 6, 0, 4) };
        var state = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Text = NetChatStateText() };
        enable.Click += (_, _) => { preferences.NetChatEnabled = enable.IsChecked == true; SaveCompanion(); state.Text = NetChatStateText(); };
        page.Children.Add(enable);

        var providerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        providerRow.Children.Add(new TextBlock { Text = "用哪家", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        var providerBox = new ComboBox { MinWidth = 300 };
        System.Windows.Automation.AutomationProperties.SetName(providerBox, "联网聊天用哪家");
        foreach (var p in NetProviders) providerBox.Items.Add(p.Title);
        providerBox.SelectedIndex = Array.IndexOf(NetProviders, CurrentProvider);
        providerRow.Children.Add(providerBox);
        page.Children.Add(providerRow);
        page.Children.Add(state);

        var keyRow = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var keyLabel = new TextBlock { Text = "Key", Width = 70, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(keyLabel, Dock.Left);
        keyRow.Children.Add(keyLabel);
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(keyButtons, Dock.Right);
        keyRow.Children.Add(keyButtons);
        var keyBox = new PasswordBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(keyBox, "联网聊天的 Key");
        keyRow.Children.Add(keyBox);
        Button Small(Panel panel, string text, Action action)
        {
            var b = BigBlueFish.Deployment.ModernUI.Button(text, action);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Padding = new Thickness(10, 3, 10, 3);
            b.MinWidth = 0;
            panel.Children.Add(b);
            return b;
        }
        void Describe() => keyBox.ToolTip = NetKey(CurrentProvider) is { } k
            ? $"已保存 {CurrentProvider.Short} 的 Key（…{k[^Math.Min(4, k.Length)..]}，只在这台电脑上加密保存）" : $"还没有保存 {CurrentProvider.Short} 的 Key";
        Small(keyButtons, "保存", () =>
        {
            if (keyBox.Password.Trim().Length == 0) { state.Text = "先把 Key 粘贴到框里再保存。"; return; }
            try { SaveNetKey(CurrentProvider, keyBox.Password); }
            catch (Exception ex) { state.Text = "保存失败：" + ex.Message; return; }
            keyBox.Clear();
            Describe();
            state.Text = "已保存（加密存在本机）。" + NetChatStateText();
        });
        Button? test = null;
        test = Small(keyButtons, "测试连接", async () =>
        {
            test!.IsEnabled = false;
            state.Text = $"正在连接 {CurrentProvider.Short}……";
            try { state.Text = await TestNetChatAsync(); }
            finally { test.IsEnabled = true; }
        });
        Small(keyButtons, "删除", () => { SaveNetKey(CurrentProvider, null); keyBox.Clear(); Describe(); state.Text = "已删除 Key。" + NetChatStateText(); });
        page.Children.Add(keyRow);

        var modelRow = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var modelLabel = new TextBlock { Text = "模型", Width = 70, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(modelLabel, Dock.Left);
        modelRow.Children.Add(modelLabel);
        var keysPage = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(keysPage, Dock.Right);
        modelRow.Children.Add(keysPage);
        Small(keysPage, "获取 Key…", () => { try { Process.Start(new ProcessStartInfo(CurrentProvider.KeysPage) { UseShellExecute = true }); } catch { } });
        var model = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            ToolTip = "OpenRouter 可以写几个模型，用逗号隔开，按顺序试；以 :free 结尾的是免费模型" };
        System.Windows.Automation.AutomationProperties.SetName(model, "联网聊天的模型");
        modelRow.Children.Add(model);
        model.LostFocus += (_, _) =>
        {
            var provider = CurrentProvider;
            string value = model.Text.Trim();
            string? custom = value.Length == 0 || value == provider.DefaultModels ? null : value;
            if (provider == DeepSeekProvider) preferences.DeepSeekModel = custom; else preferences.OpenRouterModels = custom;
            model.Text = NetChatModelText(provider);
            ResetNetChatState();
            SaveCompanion();
        };
        page.Children.Add(modelRow);
        void ShowProvider()
        {
            model.Text = NetChatModelText(CurrentProvider);
            keyBox.Clear();
            Describe();
            state.Text = NetChatStateText();
        }
        ShowProvider();
        providerBox.SelectionChanged += (_, _) =>
        {
            if (providerBox.SelectedIndex < 0) return;
            preferences.NetChatProvider = NetProviders[providerBox.SelectedIndex].Id;
            lastNetChatModel = null;
            ResetNetChatState();
            SaveCompanion();
            ShowProvider();
        };

        page.Children.Add(new TextBlock { Text = "联网时的性格（发给联网模型的人设，可以改）", Margin = new Thickness(0, 6, 0, 4) });
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
        note("OpenRouter 免费模型：到 openrouter.ai 注册免费账号、创建一个 Key（不用充值），填进来点「测试连接」。默认按顺序试 DeepSeek V4 Flash 免费版、Gemma 4 31B 免费版、Nemotron 3 Ultra 免费版："
            + "哪个现在没有服务商提供就自动跳过（DeepSeek 免费版哪天恢复就自动用上）。免费次数每天约 50 次、每分钟 20 次（UTC 零点重置），用完当天自动换回本地模型。"
            + "免费模型的服务商可能会记录对话、用来改进模型；OpenRouter 隐私设置不允许免费模型时，测试和聊天会提示。"
            + "DeepSeek 官方 API 按用量付费，需要 platform.deepseek.com 的 API Key。两家都不使用 DeepSeek 网页版——网页版的用户协议不允许用程序自动聊天。"
            + "联网时，你说的话、最近几轮对话、鱼鱼想起的相关记忆和称呼、好感度、时间会发给所选的服务；连不上时全部在本机用本地模型。"
            + "Key 用 Windows 的数据保护加密，只存在这台电脑的 data 文件夹，只发给所选服务的地址。"
            + "回复开头的【动作：…】会让她做对应的动作；聊天框开着、10 分钟没收到你的消息，她会软软地问「主人还在吗……」并轻轻甩尾鳍。"
            + "人设里的“听主人的”只限于安全、正当的事；聊天始终保持温暖健康。");
    }
}
