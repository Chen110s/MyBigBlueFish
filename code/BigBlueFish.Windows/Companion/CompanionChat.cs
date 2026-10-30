using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// Double-click chat: a small input box beside her; the local model answers in her bubble.
// Recent turns stay in this session; older ones come back through CompanionMemoryStore,
// faded like human memory.
public partial class MainWindow
{
    private Window? chatWindow;
    private readonly List<CompanionBrain.ChatMessage> chatSession = new();
    private DateTime chatSessionStartUtc = DateTime.UtcNow;
    private DateTime chatLastUtc = DateTime.MinValue;
    private bool chatBusy;

    private bool LocalChatAvailable => preferences.ChatEnabled && !PuppetActive && !exitAnimation && !companionClosed && companionReady;

    private const string ChatGuidance = """

现在主人在和鱼鱼面对面聊天（主人在输入框里打字，鱼鱼的回答显示在气泡里）。像亲近的小伙伴一样自然地回答主人刚说的话：先接住主人的话——回答问题、回应心情，再可以加一点自己的小反应或一个小问题。
回复是口语，一到三小句，一般不超过60个字；不要列表、不要长篇大论，不要说自己是AI、模型或程序。
主人问到鱼鱼不知道的事，就老实说不太懂，可以撒娇或猜一猜，但不要编造具体事实、数字或新闻。
聊天语气演示（只学语气，不要照抄）：
主人：今天好累啊 → 鱼鱼：抱抱主人！辛苦惹……先歇一会儿嘛，鱼鱼给你哼首歌～
主人：你喜欢吃什么 → 鱼鱼：大白饭！香喷喷的大白饭最好吃了，嘿嘿，主人呢？
主人：我明天要考试 → 鱼鱼：哇，主人要考试！鱼鱼给你加油，今晚早点睡哦～
""";

    private const string MemoryGuidance = """

【鱼鱼的记忆】鱼鱼的记忆和人一样会慢慢模糊。下面是此刻想起来的片段，括号里是清晰程度：
- 清楚的可以自然地提起；
- 模糊的只说个大概，用“好像”“记不太清了”这类说法，不要补全或编造细节，时间也可能记错；
- 只剩一点点印象的，就说有点印象但想不起具体内容，可以请主人再说说；
- 没有出现在这里的旧事，就是鱼鱼已经忘了——如果主人提起，老实说记不太清了。
不用刻意提到这些记忆，只在和主人的话相关时自然用上。
""";

    private bool TryOpenChat()
    {
        if (!LocalChatAvailable) return false;
        if (chatWindow != null) { chatWindow.Show(); chatWindow.Activate(); return true; }
        petPanel?.Close();
        if (crawl is { Finished: false } walking) walking.StopRequested = true; // come down to talk
        if ((DateTime.UtcNow - chatLastUtc).TotalMinutes > 30)
        {
            chatSession.Clear();
            chatSessionStartUtc = DateTime.UtcNow;
        }
        const string hint = "和鱼鱼说点什么吧～（Enter 发送，Shift+Enter 换行）";
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500,
            Height = 52, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14, Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 22, 0) };
        System.Windows.Automation.AutomationProperties.SetName(text, "和鱼鱼聊天");
        var placeholder = new TextBlock { Text = hint, Foreground = Brushes.Gray, FontSize = 13,
            Margin = new Thickness(2, 4, 22, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
        var editor = new Grid();
        editor.Children.Add(text);
        editor.Children.Add(placeholder);
        var status = new TextBlock { Text = "Enter 发送 · Esc 收起", Foreground = Brushes.SlateGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        var content = new StackPanel();
        content.Children.Add(editor);
        var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 251, 244)), BorderBrush = blue,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = content };
        var window = new Window { Title = "蓝色大肥鱼 · 聊天", Width = 340, Height = 118, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = true, Topmost = true, Owner = this, Content = frame,
            FontFamily = new FontFamily("Microsoft YaHei UI"), WindowStartupLocation = WindowStartupLocation.Manual };
        using (var theme = typeof(BigBlueFish.Deployment.ModernWindow).Assembly.GetManifestResourceStream("BigBlueFish.ModernTheme.xaml"))
        {
            if (theme != null)
            {
                var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(theme);
                window.Resources[typeof(Button)] = resources[typeof(Button)];
            }
        }
        var close = new Button { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 16,
            MinHeight = 0, MinWidth = 0, Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "收起聊天框" };
        close.Click += (_, _) => window.Close();
        editor.Children.Add(close);
        Button? send = null;
        void Send()
        {
            var message = text.Text.Trim();
            if (message.Length == 0 || chatBusy) return;
            text.Clear();
            _ = SendChatAsync(message, status, send!);
            text.Focus();
        }
        send = BigBlueFish.Deployment.ModernUI.Button("发送", Send, true);
        send.MinWidth = 58; send.MinHeight = 0; send.Margin = new Thickness(0); send.Padding = new Thickness(12, 5, 12, 5); send.FontSize = 12;
        send.IsEnabled = false;
        var actions = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(status);
        actions.Children.Add(send);
        Grid.SetColumn(send, 1);
        content.Children.Add(actions);
        text.TextChanged += (_, _) =>
        {
            placeholder.Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            send.IsEnabled = !chatBusy && !string.IsNullOrWhiteSpace(text.Text);
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; window.Close(); }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(); }
        };
        bool positioning = false;
        void Follow()
        {
            if (positioning || chatWindow != window) return;
            positioning = true;
            try
            {
                if (!IsVisible) { window.Hide(); return; }
                var area = CompanionEdgeLayout.WorkArea(this);
                var body = CompanionEdgeLayout.PetBounds(this);
                window.Width = Math.Min(340, Math.Max(160, area.Width - 16));
                double x = Left + body.Left - window.Width - 8;
                if (x < area.Left + 8) x = Left + body.Right + 8;
                double y = Top + body.Bottom - window.Height;
                window.Left = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.Width - 8));
                window.Top = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.Height - 8));
                if (!window.IsVisible) window.Show();
            }
            finally { positioning = false; }
        }
        EventHandler moved = (_, _) => Follow();
        SizeChangedEventHandler resized = (_, _) => Follow();
        DependencyPropertyChangedEventHandler visibility = (_, _) => Follow();
        LocationChanged += moved; SizeChanged += resized; IsVisibleChanged += visibility;
        chatWindow = window;
        window.Closed += (_, _) =>
        {
            LocationChanged -= moved; SizeChanged -= resized; IsVisibleChanged -= visibility;
            if (chatWindow == window) chatWindow = null;
            crawlResumeAt = DateTime.UtcNow.AddSeconds(15);
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        };
        window.Loaded += (_, _) => text.Focus();
        Follow();
        window.Activate();
        return true;
    }

    private async Task SendChatAsync(string message, TextBlock status, Button send)
    {
        chatBusy = true;
        send.IsEnabled = false;
        chatLastUtc = DateTime.UtcNow;
        NoteInteraction();
        bond.RollDay(DateTime.Now);
        bond.Chats++;
        if (bond.Chats <= 20) AddAffection(.6);
        CaptureNickname(message);
        try
        {
            var reminder = CompanionReminderParser.LooksLikeRequest(message) ? TryCreateReminder(message) : null;
            if (reminder != null) { ChatReply(message, reminder, "点击回应-元气挥手"); return; }
            if (Regex.IsMatch(message, "^(好了|算了)?(结束|停止|不)(专注|番茄钟?)"))
            {
                if (FocusActive) StopFocus(false); else ChatReply(message, "现在没有在专注哦～想开始的话就说「开始专注」吧！");
                return;
            }
            if (Regex.IsMatch(message, "^(开始|我要|帮我|来)?(一个|个)?(专注|番茄钟?)"))
            {
                if (!FocusActive) StartFocus();
                return;
            }
            if (TryDeltaForceChat(message)) return;
            if (!preferences.ModelEnabled || !brain.ModelAvailable)
            {
                ChatReply(message, CannedChatReply(message));
                return;
            }
            if (!brain.CanRunOnCurrentPower)
            {
                ChatReply(message, "现在是电池供电，鱼鱼的小脑袋按设置休息啦……插上电再和鱼鱼聊天好不好～");
                return;
            }
            status.Text = "鱼鱼在想……";
            SupersedeModelSpeech();
            BeginModelSpeech();
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed) await Task.Delay(150);
            if (companionClosed) return;
            if (brain.Busy)
            {
                EndModelSpeech();
                ChatReply(message, "唔……鱼鱼的小脑袋还在忙，等一下下再说好不好～");
                return;
            }
            var messages = BuildChatMessages(message);
            // Any waiting speech/weather request loses its turn; clear its flags so it cannot get stuck.
            speechRequestPending = weatherRequestPending = false;
            long version = ++speechRequestVersion;
            string reply = await brain.GenerateChat(messages, partial => ShowCompanionPartial(partial, version));
            if (version == speechRequestVersion) EndModelSpeech();
            if (companionClosed) return;
            if (reply.Length == 0)
            {
                ChatReply(message, "唔……鱼鱼刚刚走神了，" + bond.Address + "可以再说一遍吗？", remember: false);
                return;
            }
            if (version == speechRequestVersion) FinishCompanionPartial(reply);
            chatSession.Add(new CompanionBrain.ChatMessage("user", message));
            chatSession.Add(new CompanionBrain.ChatMessage("assistant", reply));
            if (chatSession.Count > 12) chatSession.RemoveRange(0, chatSession.Count - 12);
            RememberChat(message, reply);
        }
        finally
        {
            chatBusy = false;
            chatLastUtc = DateTime.UtcNow;
            status.Text = "Enter 发送 · Esc 收起";
        }
    }

    // Replies that do not need the model still go through the bubble and the session/memory.
    private void ChatReply(string message, string reply, string? animation = null, bool remember = true)
    {
        Say(reply, animation);
        chatSession.Add(new CompanionBrain.ChatMessage("user", message));
        chatSession.Add(new CompanionBrain.ChatMessage("assistant", reply));
        if (chatSession.Count > 12) chatSession.RemoveRange(0, chatSession.Count - 12);
        if (remember) RememberChat(message, reply);
    }

    private void RememberChat(string message, string reply)
    {
        if (!preferences.MemoryEnabled || memory == null) return;
        memory.Remember(message, reply);
        memory.Save();
    }

    private IReadOnlyList<CompanionBrain.ChatMessage> BuildChatMessages(string message)
    {
        var system = new StringBuilder();
        system.Append(CompanionDialogue.Tone);
        system.Append(ChatGuidance);
        system.Append('\n').Append(BondContext());
        if (preferences.MemoryEnabled && memory != null)
        {
            var recalled = memory.Recall(message, chatSessionStartUtc);
            if (recalled.Count > 0)
                system.Append(MemoryGuidance).Append(string.Join("\n", recalled.Select(r => "· " + r)));
            else
                system.Append("\n【鱼鱼的记忆】此刻没有想起什么相关的旧事；如果主人提起以前的事，就说有点印象但记不清了，可以请主人再讲讲。");
            memory.Save();
        }
        system.Append(DeltaForceChatContext(message));
        if (GameActive) system.Append($"\n【现在】主人正在玩《{gameName}》，回答要短，别打扰主人打游戏。");
        if (FocusActive) system.Append($"\n【现在】主人正在专注（还剩 {FocusMinutesLeft} 分钟），回答简短，鼓励主人继续专注。");
        var list = new List<CompanionBrain.ChatMessage> { new("system", system.ToString()) };
        list.AddRange(chatSession.TakeLast(12));
        list.Add(new CompanionBrain.ChatMessage("user", message));
        return list;
    }

    private static readonly Regex NicknamePattern = new(@"^(?:(?:以后)?就?叫我|我叫|我的名字(?:是|叫))\s*([\p{L}\p{N}]{1,8})[\s,，。.!！~～]*$");
    private static readonly HashSet<string> NotNames = new() { "起床", "吃饭", "睡觉", "一下", "回来", "什么", "啥", "过去", "出去", "名字" };

    private void CaptureNickname(string message)
    {
        if (CompanionReminderParser.LooksLikeRequest(message)) return;
        var match = NicknamePattern.Match(message.Trim());
        if (!match.Success) return;
        string name = Regex.Replace(match.Groups[1].Value, "[吧啦呀哦哈嘛呢噢喔]+$", "");
        if (name.Length == 0 || name.Length > 6 || NotNames.Contains(name) || Regex.IsMatch(name, "^[了过着的个在去来]")) return;
        bond.Nickname = name == "主人" ? null : name;
        SaveBondSoon();
    }

    private string CannedChatReply(string message)
    {
        if (Regex.IsMatch(message, "你好|嗨|哈喽|hello|hi", RegexOptions.IgnoreCase)) return $"{bond.Address}好呀～鱼鱼在这儿！";
        if (Regex.IsMatch(message, "喜欢你|爱你")) return "鱼、鱼鱼也最喜欢" + bond.Address + "了！嘿嘿～";
        if (Regex.IsMatch(message, "累|困|难过|伤心")) return "抱抱" + bond.Address + "！辛苦啦，先休息一下嘛～";
        if (Regex.IsMatch(message, "饿|吃")) return "鱼鱼也饿了……想吃大白饭！右键可以喂鱼鱼零食哦～";
        return "鱼鱼的小脑袋（本地模型）还没开启呢……在设置→模型与供电里启用后，鱼鱼就能好好陪你聊天啦～";
    }
}
