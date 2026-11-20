using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

// Multi-turn chat with the same local llama.cpp runner used for the short speech lines.
internal sealed partial class CompanionBrain
{
    private string? runnerModel; // the model file the running llama-server has loaded

    internal string? ModelPath => BigBlueFish.Deployment.ModelStore.Resolve(Environment.CurrentDirectory);

    // Bigger local models load and answer more slowly: every GB above 3 adds 20 s to the time limits (the default 4B model
    // gets none, a 9B Q4 model about a minute).
    internal TimeSpan ModelAllowance
    {
        get
        {
            try { return ModelPath is { } path ? AllowanceFor(new FileInfo(path).Length) : TimeSpan.Zero; }
            catch { return TimeSpan.Zero; }
        }
    }

    internal static TimeSpan AllowanceFor(long modelBytes) => TimeSpan.FromSeconds(Math.Max(0, modelBytes / 1073741824.0 - 3) * 20);

    // llama-server --list-devices: "Vulkan0: NVIDIA GeForce RTX 4060 (8188 MiB, 7524 MiB free)" lines, "" when it fails.
    internal Task<string> ListDevices(CancellationToken token) => gpuDevices(token);

    // Sends a full message list (system + recent turns + the owner's message) and streams the reply. clean turns the raw
    // text into what the bubble shows (by default CleanChat); it sees the whole text so far each time.
    internal async Task<string> GenerateChat(IReadOnlyList<ChatMessage> messages, Action<string>? onPartial = null, Func<string, string>? clean = null)
    {
        if (disposed || Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        var allowance = ModelAllowance;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(120) + allowance + allowance);
        var token = active.Token;
        try
        {
            await EnsureRunner(token);
            Status = "正在和主人聊天";
            var text = await CompleteChat(messages, token, onPartial, clean: clean);
            Status = ReadyStatus("本地模型就绪");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException)
        {
            StopRunner();
            Status = "聊天已取消";
            return "";
        }
        catch (Exception e)
        {
            StopRunner(); RecordFailure("聊天", e);
            Status = e is IOException ? e.Message : "暂时无法聊天，稍后重试";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    // An answer that is not shown and not tidied up as a chat line (帮我做事 asks for steps as JSON). "" when there was none.
    internal async Task<string> GeneratePlain(IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        if (disposed || Busy || !CanRunOnCurrentPower) return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(60) + ModelAllowance);
        var token = active.Token;
        try
        {
            await EnsureRunner(token);
            Status = "正在想该怎么做";
            string text = await CompleteChat(messages, token, null, maxTokens, .2, plain: true);
            Status = ReadyStatus("本地模型就绪");
            return text;
        }
        catch (OperationCanceledException)
        {
            StopRunner();
            return "";
        }
        catch (Exception e)
        {
            StopRunner(); RecordFailure("拆步骤", e);
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    private async Task<string> CompleteChat(IReadOnlyList<ChatMessage> messages, CancellationToken token, Action<string>? onPartial,
        int maxTokens = 220, double temperature = .7, bool plain = false, Func<string, string>? clean = null)
    {
        clean ??= CleanChat;
        var payload = new
        {
            model = "local",
            messages = messages.ToArray(),
            temperature,
            top_p = .9,
            max_tokens = maxTokens,
            stream = true,
            cache_prompt = true,
            chat_template_kwargs = new { enable_thinking = false }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await local.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var raw = new StringBuilder();
        var lastPartial = "";
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var chunk = JsonDocument.Parse(data);
                if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                string piece = "";
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
                    piece = deltaContent.GetString() ?? "";
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent))
                    piece = messageContent.GetString() ?? "";
                if (piece.Length == 0) continue;
                raw.Append(piece);
                if (plain) continue;
                var partial = clean(raw.ToString());
                if (partial.Length > 0 && partial != lastPartial)
                {
                    lastPartial = partial;
                    onPartial?.Invoke(partial);
                }
            }
            catch (JsonException) { }
        }
        return plain ? Regex.Replace(raw.ToString(), @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim() : clean(raw.ToString());
    }

    // Like Clean, but allows a slightly longer conversational reply and strips role prefixes.
    internal static string CleanChat(string text)
    {
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase)) return "";
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '“', '”');
        text = Regex.Replace(text, @"^(鱼鱼|蓝色大肥鱼)\s*[:：]\s*", "");
        if (!Regex.IsMatch(text, @"[\p{L}\p{N}]") || Regex.IsMatch(text, @"(.)\1{8}")) return "";
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= 160) return text;
        var shortText = string.Concat(runes.Take(158).Select(x => x.ToString()));
        var end = shortText.LastIndexOfAny(new[] { '。', '！', '？', '～', '~' });
        return end >= 30 ? shortText[..(end + 1)] : shortText + "…";
    }
}
