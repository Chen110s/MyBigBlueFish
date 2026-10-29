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
    // Sends a full message list (system + recent turns + the owner's message) and streams the reply.
    internal async Task<string> GenerateChat(IReadOnlyList<ChatMessage> messages, Action<string>? onPartial = null)
    {
        if (disposed || Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = active.Token;
        try
        {
            await EnsureRunner(token);
            Status = "正在和主人聊天";
            var text = await CompleteChat(messages, token, onPartial);
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

    private async Task<string> CompleteChat(IReadOnlyList<ChatMessage> messages, CancellationToken token, Action<string>? onPartial)
    {
        var payload = new
        {
            model = "local",
            messages = messages.ToArray(),
            temperature = .7,
            top_p = .9,
            max_tokens = 220,
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
                var partial = CleanChat(raw.ToString());
                if (partial.Length > 0 && partial != lastPartial)
                {
                    lastPartial = partial;
                    onPartial?.Invoke(partial);
                }
            }
            catch (JsonException) { }
        }
        return CleanChat(raw.ToString());
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
