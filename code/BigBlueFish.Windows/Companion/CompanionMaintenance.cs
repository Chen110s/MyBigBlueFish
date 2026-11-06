using System;
using System.Diagnostics;
using System.Windows;
using System.Threading.Tasks;
using BigBlueFish.Deployment;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private ModelInstallWindow? modelInstaller;
    private bool checkingUpdates;
    private UpdateWindow? updateWindow;
    private AvailableUpdate? availableUpdate;
    private string updateStatus = "";
    private void OpenModelInstaller()
    {
        if (exitAnimation || companionClosed) return;
        if (modelInstaller != null) { modelInstaller.Activate(); return; }
        InvalidateSpeechRequests(true);
        var dialog = modelInstaller = new ModelInstallWindow(Environment.CurrentDirectory);
        if (companionSettings?.IsVisible == true) dialog.Owner = companionSettings;
        dialog.Closed += (_, _) => {
            if (modelInstaller == dialog) modelInstaller = null;
            if (companionClosed || exitAnimation || exitReady) return;
            brain.RefreshModelAvailability();
            nextWarmup = DateTime.MinValue;
            nextSpeech = DateTime.UtcNow.AddSeconds(20);
        };
        dialog.Show();
    }

    private async Task CheckForUpdates(bool interactive)
    {
        if (checkingUpdates || companionClosed || exitAnimation) return;
        checkingUpdates = true; updateStatus = "正在检查更新…";
        try
        {
            var version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            availableUpdate = await UpdateService.Check(version);
            if (companionClosed || exitAnimation) return;
            updateStatus = availableUpdate == null ? "当前已是最新版本" : "发现新版本 " + availableUpdate.Version;
            if (availableUpdate != null)
            {
                if (interactive) OpenUpdateWindow();
                else notifyIcon.ShowBalloonTip(8000, "蓝色大肥鱼有新版本啦", "官方版 " + availableUpdate.Version + " 已发布（更新会覆盖陪伴增强包，点开看看）", System.Windows.Forms.ToolTipIcon.Info);
            }
            else if (interactive) ModernUI.Choice(companionSettings, "检查更新", updateStatus, "确定");
        }
        catch (Exception error)
        {
            updateStatus = "暂时无法检查更新，可以稍后重试";
            if (interactive) ModernUI.Error(companionSettings, "检查更新未完成", error);
        }
        finally { checkingUpdates = false; }
    }

    private void OpenUpdateWindow()
    {
        if (availableUpdate == null || companionClosed || exitAnimation) return;
        if (updateWindow != null) { updateWindow.Activate(); return; }
        // 陪伴增强包: the official installer replaces the program files and, with them, every companion feature;
        // the official version also drops the companion settings from preferences.json. Ask before going on.
        if (ModernUI.Choice(companionSettings, "更新会覆盖陪伴增强包",
                $"官方新版本 {availableUpdate.Version} 会替换程序文件：爬行、喂食与陪玩、金价、三角洲、形象包切换、DeepSeek 网页版等增强功能都会消失，"
                + "这些功能的设置也会被清掉（好感度、聊天记忆、提醒、金价记录不受影响）。\n\n建议先不更新，等陪伴增强包适配新版本。仍要更新吗？",
                "先不更新", "仍要更新") != 1) return;
        var dialog = updateWindow = new UpdateWindow(availableUpdate, AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (companionSettings?.IsVisible == true) dialog.Owner = companionSettings;
        dialog.Closed += (_, _) => {
            if (updateWindow == dialog) updateWindow = null;
            if (dialog.InstallerStarted && !companionClosed && !exitAnimation && !exitReady) base.Close();
        };
        dialog.Show();
    }
}
