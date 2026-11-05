using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace BigBlueFish.Windows;
public partial class MainWindow
{
    // When one of her clips fails she checks herself, writes what she found to companion-errors.log and tries again
    // (after 2, 8 and 18 seconds). Only when it keeps failing does she stop and say why; even then she tries once a
    // minute and comes back by herself as soon as a clip plays again.
    private const string CompanionErrorLog = "companion-errors.log";
    private int animationFailures;
    private bool animationStopped;
    private DispatcherTimer? animationRetry;
    private DateTime nextAnimationWarning;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static void LogCompanionError(string text)
    {
        try
        {
            var log = new FileInfo(CompanionErrorLog);
            if (log.Exists && log.Length > 256 * 1024) File.Move(CompanionErrorLog, CompanionErrorLog + ".old", true);
            File.AppendAllText(CompanionErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    // Logged at most once an hour: when other software keeps the pet in its own job, every clip would say the same.
    private void AnimationWarning(string text)
    {
        if (DateTime.UtcNow < nextAnimationWarning) return;
        nextAnimationWarning = DateTime.UtcNow.AddHours(1);
        LogCompanionError(text);
    }

    private void AnimationFailed(string error)
    {
        string file = videoPlayer?.CurrentFile ?? "";
        string reason = AnimationSelfCheck(file, error);
        LogCompanionError($"动画播放失败：{error}｜动作 {Path.GetFileNameWithoutExtension(file)}｜自检：{reason}｜{ProcessResources()}");
        if (exitAnimation) { exitReady = true; base.Close(); return; }
        if (RecoverFromSkinFailure()) return;
        animationFailures++;
        if (animationFailures > 3)
        {
            companionReady = false;
            if (!animationStopped)
            {
                animationStopped = true;
                ShowCompanionNotice($"动画暂时播放不了：{reason}。鱼鱼每分钟会再试一次，好了会自己恢复～（详情见 data\\companion-errors.log）");
            }
        }
        if (animationRetry == null)
        {
            animationRetry = new DispatcherTimer();
            animationRetry.Tick += (_, _) => RetryAnimation();
        }
        animationRetry.Stop();
        animationRetry.Interval = TimeSpan.FromSeconds(animationFailures <= 3 ? 2 * animationFailures * animationFailures : 60);
        animationRetry.Start();
    }

    private void RetryAnimation()
    {
        animationRetry?.Stop();
        if (companionClosed || exitAnimation || videoPlayer == null) return;
        if (animationStopped) companionReady = true; // PlayAnimation needs it; another failure stops her again
        PlayIdle();
    }

    // Half a second of frames means the decoder works again.
    private void AnimationPlayed(double _)
    {
        if (animationFailures == 0 || videoPlayer == null || videoPlayer.Frames < CompanionVideoPlayer.Fps / 2) return;
        animationFailures = 0;
        animationRetry?.Stop();
        LogCompanionError("动画已恢复正常播放");
        if (!animationStopped) return;
        animationStopped = false;
        ShowCompanionNotice("鱼鱼的动画恢复啦～刚才是动画解码器临时出了状况，现在好了。");
    }

    // What most likely went wrong, in words: a missing decoder or clip, or what the decoder's exit code means.
    private static string AnimationSelfCheck(string file, string error)
    {
        string decoder = Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe");
        if (!File.Exists(decoder)) return "找不到动画解码器 media\\ffmpeg.exe（可能被安全软件隔离了，请从隔离区恢复或重新安装）";
        if (file.Length > 0 && !File.Exists(file)) return $"找不到动画文件「{Path.GetFileName(file)}」";
        var exit = Regex.Match(error, "退出码 0x([0-9A-F]{8})");
        string code = exit.Success ? exit.Groups[1].Value : "";
        string said = error.Contains('：') ? error[(error.IndexOf('：') + 1)..].Trim() : "";
        return code switch
        {
            "C0000142" => "解码器启动时初始化失败（0xC0000142），通常是系统资源暂时用尽，关掉一些程序或重启电脑就好",
            "C0000017" or "C000012D" => "内存不足，解码器没能启动，关掉一些程序再试",
            "C0000022" => "解码器被拒绝运行（0xC0000022），可能被安全软件拦截了",
            "C0000005" => "解码器意外崩溃（0xC0000005）",
            "" when error.Contains("帧") => "解码器中途停了，动画帧数据不完整",
            "" => "动画文件和解码器都在，但 Windows 没能启动解码器：" + error,
            "00000000" => $"「{Path.GetFileName(file)}」没有解出画面，文件可能损坏了",
            _ when said.Length > 0 => $"解码器报错：{said}",
            _ => $"解码器被意外结束了（退出码 0x{code}），可能是安全软件或系统拦截"
        };
    }

    // The pet's own resource use when it failed, to tell a leak from a busy machine.
    private static string ProcessResources()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return $"GDI {GetGuiResources(self.Handle, 0)}、USER {GetGuiResources(self.Handle, 1)}、句柄 {self.HandleCount}、" +
                $"内存 {self.PrivateMemorySize64 >> 20} MB、已运行 {(DateTime.Now - self.StartTime).TotalHours:0.0} 小时";
        }
        catch (Exception ex) { return "资源信息读取失败：" + ex.Message; }
    }
}
