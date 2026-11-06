using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// DeepSeek 网页版: the official chat.deepseek.com in a window of the pet (Microsoft Edge WebView2). The owner
// logs in and chats there as in any browser; the pet never types, sends or reads anything on her own. Only
// when the owner clicks 「保存这次对话」 is the conversation on the page copied into the chat folder.
// 聊天记录: every round in the pet's own chat box is also appended to a daily Markdown file in that folder.
public partial class MainWindow
{
    private const string DeepSeekWebUrl = "https://chat.deepseek.com/";
    private const string WebView2Download = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private Window? deepSeekWindow;
    private static bool webView2ResolverInstalled;

    // ---------------------------------------------------------------- the chat folder
    internal string DefaultChatFolder => Path.GetFullPath("chats"); // data\chats (the working directory is data)

    // The chosen folder if it still exists, otherwise data\chats.
    internal string ChatFolder => !string.IsNullOrWhiteSpace(preferences.ChatLogFolder) && Directory.Exists(preferences.ChatLogFolder)
        ? preferences.ChatLogFolder : DefaultChatFolder;

    private static string QuoteBlock(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Trim().Split('\n').Select(line => "> " + line));

    // One round of the pet's chat box, appended to 鱼鱼聊天-yyyy-MM-dd.md.
    private void LogChat(string message, string reply, string source)
    {
        if (!preferences.ChatLogEnabled || string.IsNullOrWhiteSpace(message)) return;
        try
        {
            var now = DateTime.Now;
            string folder = ChatFolder;
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"鱼鱼聊天-{now:yyyy-MM-dd}.md");
            bool fresh = !File.Exists(file);
            var text = new StringBuilder();
            if (fresh) text.Append($"# 和鱼鱼的聊天 · {now:yyyy-MM-dd}\n\n");
            text.Append($"### {now:HH:mm:ss}　{source}\n\n**你**\n\n{QuoteBlock(message)}\n\n**鱼鱼**\n\n{QuoteBlock(reply.Length == 0 ? "（没有回话）" : reply)}\n\n");
            File.AppendAllText(file, text.ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(fresh));
        }
        catch (Exception ex) { LogCompanionError("聊天记录保存失败：" + ex.Message); }
    }

    private void OpenChatFolder()
    {
        try
        {
            Directory.CreateDirectory(ChatFolder);
            Process.Start(new ProcessStartInfo(ChatFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Say("打不开聊天记录文件夹：" + ex.Message); }
    }

    // Settings (互动与关怀 → 聊天与记忆): the log switch, the folder and the web page.
    private void AddChatLogSettings(Panel page, Action<string> note)
    {
        var log = new CheckBox { Content = "把和鱼鱼的每一轮聊天另存一份（Markdown，每天一个文件）", IsChecked = preferences.ChatLogEnabled, Margin = new Thickness(0, 6, 0, 4) };
        log.Click += (_, _) => { preferences.ChatLogEnabled = log.IsChecked == true; SaveCompanion(); };
        page.Children.Add(log);
        var folderText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        void Describe() => folderText.Text = "保存到：" + ChatFolder + (string.IsNullOrWhiteSpace(preferences.ChatLogFolder) ? "（默认）"
            : Directory.Exists(preferences.ChatLogFolder) ? "" : "（你选的文件夹不在了，暂时存到默认位置）");
        Describe();
        page.Children.Add(folderText);
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Button Add(string text, Action action)
        {
            var button = BigBlueFish.Deployment.ModernUI.Button(text, action);
            button.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(button);
            return button;
        }
        Add("选择文件夹…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "聊天记录保存到哪个文件夹", InitialDirectory = ChatFolder };
            if (dialog.ShowDialog(companionSettings) != true || !Directory.Exists(dialog.FolderName)) return;
            preferences.ChatLogFolder = dialog.FolderName;
            SaveCompanion();
            Describe();
        });
        Add("打开文件夹", OpenChatFolder);
        Add("恢复默认", () => { preferences.ChatLogFolder = ""; SaveCompanion(); Describe(); });
        Add("打开 DeepSeek 网页版…", ShowDeepSeekWeb);
        page.Children.Add(row);
        note("DeepSeek 网页版：在鱼鱼的窗口里打开官方网页版（chat.deepseek.com），你自己登录、像平时一样免费聊；鱼鱼不会替你发消息，也不会自己读取页面。"
            + "点窗口上方的「保存这次对话」时，才把当前页面上的对话另存一份到上面的文件夹（文件名以 DeepSeek 开头）。需要 Microsoft Edge WebView2 运行时（Windows 11 自带）。"
            + "聊天记录和网页的登录信息都只保存在本机。");
    }

    // ---------------------------------------------------------------- the web page window
    // The WebView2 assemblies are installed next to the program but are not listed in its deps.json.
    private static void InstallWebView2Resolver()
    {
        if (webView2ResolverInstalled) return;
        webView2ResolverInstalled = true;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name == null || !name.Name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal)) return null;
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    private void ShowDeepSeekWeb()
    {
        if (deepSeekWindow != null)
        {
            if (deepSeekWindow.WindowState == WindowState.Minimized) deepSeekWindow.WindowState = WindowState.Normal;
            deepSeekWindow.Show();
            deepSeekWindow.Activate();
            return;
        }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.Web.WebView2.Core.dll"))
            || !File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.Web.WebView2.WinForms.dll")))
        {
            Say("打开网页版需要的组件还没装好，请重新运行一次 Build-EdgeCrawl.bat～");
            return;
        }
        InstallWebView2Resolver();
        try { CreateDeepSeekWindow(); }
        catch (Exception ex)
        {
            LogCompanionError("DeepSeek 网页版打开失败：" + ex);
            Say("DeepSeek 网页版没能打开：" + ex.Message);
        }
    }

    // Kept out of line so the WebView2 types are only loaded after the resolver above is in place.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateDeepSeekWindow()
    {
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var webView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = webView };
        var status = new TextBlock { Foreground = Brushes.SlateGray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0), Text = "正在打开 DeepSeek 网页版……" };
        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8), LastChildFill = true };
        Button Tool(string text, Action action, bool primary = false)
        {
            var button = BigBlueFish.Deployment.ModernUI.Button(text, action, primary);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Padding = new Thickness(12, 4, 12, 4);
            DockPanel.SetDock(button, Dock.Left);
            bar.Children.Add(button);
            return button;
        }
        var window = new Window
        {
            Title = "DeepSeek 网页版 · 蓝色大肥鱼", Width = Math.Min(1080, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(800, SystemParameters.WorkArea.Height - 40), MinWidth = 520, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true,
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)), FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        using (var theme = typeof(BigBlueFish.Deployment.ModernWindow).Assembly.GetManifestResourceStream("BigBlueFish.ModernTheme.xaml"))
            if (theme != null) window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(theme));
        var save = Tool("保存这次对话", () => _ = SaveWebConversation(webView, status), true);
        save.IsEnabled = false;
        Tool("打开保存文件夹", OpenChatFolder);
        Tool("刷新", () => { try { webView.CoreWebView2?.Reload(); } catch { } });
        Tool("在浏览器中打开", () =>
        {
            try { Process.Start(new ProcessStartInfo(webView.CoreWebView2?.Source ?? DeepSeekWebUrl) { UseShellExecute = true }); } catch { }
        });
        bar.Children.Add(status);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(214, 220, 234)), BorderThickness = new Thickness(0, 1, 0, 0), Child = host });
        window.Content = root;
        deepSeekWindow = window;
        window.Closed += (_, _) =>
        {
            if (deepSeekWindow == window) deepSeekWindow = null;
            try { webView.Dispose(); } catch { }
            host.Dispose();
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                // Logins and cookies stay in data\webview2 on this computer.
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.GetFullPath("webview2"));
                await webView.EnsureCoreWebView2Async(environment);
                var core = webView.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                // Pop-ups (log-in pages, links) open in the same window, or in the owner's browser when they leave DeepSeek.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase)) core.Navigate(e.Uri);
                    else try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                };
                core.DocumentTitleChanged += (_, _) => window.Title = (string.IsNullOrWhiteSpace(core.DocumentTitle) ? "DeepSeek 网页版" : core.DocumentTitle) + " · 蓝色大肥鱼";
                core.NavigationCompleted += (_, e) => status.Text = e.IsSuccess ? "登录后像平时一样聊天；想留一份时点「保存这次对话」。" : "页面没能打开，检查一下网络后点「刷新」。";
                save.IsEnabled = true;
                core.Navigate(DeepSeekWebUrl);
            }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                status.Text = "这台电脑还没有 Microsoft Edge WebView2 运行时，装好后再打开这个窗口。";
                Tool("下载 WebView2", () => { try { Process.Start(new ProcessStartInfo(WebView2Download) { UseShellExecute = true }); } catch { } }, true);
                bar.Children.Remove(status);
                bar.Children.Add(status);
            }
            catch (Exception ex)
            {
                status.Text = "网页没能加载：" + ex.Message;
                LogCompanionError("DeepSeek 网页版加载失败：" + ex);
            }
        };
        window.Show();
        window.Activate();
        Say("鱼鱼帮你打开了 DeepSeek 网页版～登录后就能聊，想留一份记得点「保存这次对话」哦。", "点击回应-元气挥手");
    }

    // Reads the conversation that is on the page (only now, because the owner asked) and saves it as Markdown.
    private const string ConversationScript = """
        (() => {
          const tidy = s => (s || '').replace(/ /g, ' ').replace(/\n{3,}/g, '\n\n').trim();
          const items = [];
          for (const m of document.querySelectorAll('.ds-message')) {
            const text = tidy(m.innerText);
            if (text) items.push({ role: m.querySelector('.ds-markdown') ? 'DeepSeek' : '你', text });
          }
          if (items.length === 0) {
            const main = document.querySelector('main') || document.querySelector('[role="main"]');
            let best = main, size = main ? main.innerText.length : 0;
            if (!best) for (const el of document.querySelectorAll('div')) {
              if (el.querySelector('textarea') && el.innerText.length > size) { best = el; size = el.innerText.length; }
            }
            const text = tidy((best || document.body).innerText);
            if (text) items.push({ role: '页面', text });
          }
          return JSON.stringify({ title: document.title || '', url: location.href, items });
        })()
        """;

    private sealed record WebConversation(string Title, string Url, List<WebMessage> Items);
    private sealed record WebMessage(string Role, string Text);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async System.Threading.Tasks.Task SaveWebConversation(Microsoft.Web.WebView2.WinForms.WebView2 webView, TextBlock status)
    {
        try
        {
            if (webView.CoreWebView2 == null) { status.Text = "页面还没加载好。"; return; }
            string raw = await webView.CoreWebView2.ExecuteScriptAsync(ConversationScript);
            string json = JsonSerializer.Deserialize<string>(raw) ?? "";
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var conversation = JsonSerializer.Deserialize<WebConversation>(json, options);
            if (conversation == null || conversation.Items.Count == 0) { status.Text = "这一页上还没有对话内容。"; return; }
            var now = DateTime.Now;
            string title = Regex.Replace(conversation.Title, @"\s*[-–—|]\s*DeepSeek\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (title.Length == 0) title = "对话";
            var text = new StringBuilder();
            text.Append($"# DeepSeek 网页版 · {title}\n\n保存时间：{now:yyyy-MM-dd HH:mm:ss}　网址：{conversation.Url}\n\n");
            foreach (var item in conversation.Items)
                text.Append($"## {item.Role}\n\n{item.Text.Replace("\r\n", "\n").Trim()}\n\n");
            string safe = Regex.Replace(title, @"[\\/:*?""<>|\r\n\t]", "_");
            if (safe.Length > 40) safe = safe[..40];
            string folder = ChatFolder;
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"DeepSeek-{now:yyyyMMdd-HHmmss}-{safe}.md");
            await File.WriteAllTextAsync(file, text.ToString().Replace("\n", Environment.NewLine), new UTF8Encoding(true));
            status.Text = $"已保存 {conversation.Items.Count} 段：{file}";
            Say("对话存好啦～放在聊天记录文件夹里了。", "轻快记录");
        }
        catch (Exception ex)
        {
            status.Text = "保存失败：" + ex.Message;
            LogCompanionError("DeepSeek 对话保存失败：" + ex);
        }
    }
}
