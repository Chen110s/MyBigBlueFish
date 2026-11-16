using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// The Windows side of 帮我做事: keyboard input, windows, volume.
internal static class CompanionTaskNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    // INPUT on 64-bit Windows: the type, then the union at offset 8; its largest member (the mouse) makes 40 bytes in all.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyInput Key; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool LockWorkStation();

    private const uint Keyboard = 1, KeyUp = 2, Unicode = 4, Extended = 1;

    private static Input Key(ushort vk, bool up)
    {
        // the arrows, Home / End / PgUp / PgDn / Insert / Delete and the Windows key are "extended" keys
        bool extended = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B;
        return new Input { Type = Keyboard, Key = new KeyInput { Vk = vk, Flags = (up ? KeyUp : 0) | (extended ? Extended : 0) } };
    }

    private static Input Character(char c, bool up) =>
        new() { Type = Keyboard, Key = new KeyInput { Scan = c, Flags = Unicode | (up ? KeyUp : 0) } };

    private static bool Send(List<Input> inputs) =>
        inputs.Count == 0 || SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) == inputs.Count;

    // The keys of a combination go down in order and come up in reverse.
    internal static bool Press(IReadOnlyList<ushort> keys)
    {
        var inputs = keys.Select(vk => Key(vk, false)).ToList();
        inputs.AddRange(keys.Reverse().Select(vk => Key(vk, true)));
        return Send(inputs);
    }

    // Text as Unicode key events (any language, no input method needed); a line break is the Enter key, a tab the Tab key.
    internal static bool Type(string text)
    {
        var inputs = new List<Input>();
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c is '\n' or '\t')
            {
                ushort vk = c == '\n' ? (ushort)0x0D : (ushort)0x09;
                inputs.Add(Key(vk, false));
                inputs.Add(Key(vk, true));
            }
            else
            {
                inputs.Add(Character(c, false));
                inputs.Add(Character(c, true));
            }
        }
        return Send(inputs);
    }

    internal static string ProcessName(IntPtr window)
    {
        try
        {
            CompanionNative.GetWindowThreadProcessId(window, out uint id);
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    internal static bool IsOwn(IntPtr window)
    {
        CompanionNative.GetWindowThreadProcessId(window, out uint id);
        return id == Environment.ProcessId;
    }

    // ---- master volume (Core Audio)
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // level 0–100 sets it; otherwise up / down (10 points) / mute / unmute. Returns the volume afterwards (0–100), or −1 when
    // the audio device could not be reached (then the caller falls back to the volume keys).
    internal static int Volume(double level, string action)
    {
        object? enumerator = null, endpoint = null;
        IMMDevice? device = null;
        try
        {
            enumerator = new DeviceEnumerator();
            if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return -1;
            var id = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref id, 23, IntPtr.Zero, out endpoint) != 0 || endpoint is not IAudioEndpointVolume volume) return -1;
            var none = Guid.Empty;
            if (volume.GetMasterVolumeLevelScalar(out float now) != 0) return -1;
            if (action == "mute") volume.SetMute(true, ref none);
            else if (action == "unmute") volume.SetMute(false, ref none);
            else
            {
                float wanted = action == "up" ? now + .1f : action == "down" ? now - .1f : (float)(level / 100);
                if (volume.SetMasterVolumeLevelScalar(Math.Clamp(wanted, 0, 1), ref none) != 0) return -1;
                if (wanted > 0) volume.SetMute(false, ref none);
            }
            volume.GetMasterVolumeLevelScalar(out now);
            return (int)Math.Round(now * 100);
        }
        catch { return -1; }
        finally
        {
            foreach (object? com in new[] { endpoint, device, enumerator })
                if (com != null) try { Marshal.ReleaseComObject(com); } catch { }
        }
    }
}

// 帮我做事: what the owner types in the chat box is turned into steps (CompanionTasks) and carried out here. Only the owner's
// own words start anything; what is on the screen, on a web page or in a model's answer never does. Steps that use the
// keyboard, close a window or shut the computer down are shown first and wait for 「执行」; steps the chat model worked out
// always are. She does not type into password boxes, command-line windows or games, and does not open scripts or installers.
public partial class MainWindow
{
    private sealed record PendingTask(string Message, List<TaskStep> Steps, DateTime Expires);
    // Window: where the keys go (the window the last 打开 / 搜索 brought up). Lost: something was opened but its window never
    // came to the front — then nothing is typed, rather than into whatever was in front before.
    private sealed class TaskRun { public IntPtr Window; public string? Note; public bool Lost; public int WaitSeconds = 3; }

    private PendingTask? pendingTask;
    private bool taskRunning;
    private List<(string Name, string Path)>? taskShortcuts;
    private DateTime taskShortcutsAt = DateTime.MinValue;
    private Dictionary<string, string>? taskAliases;
    private string taskAliasesText = "\0";

    // ---------------------------------------------------------------- finding programs
    private Dictionary<string, string> TaskAliases()
    {
        if (taskAliases == null || taskAliasesText != preferences.TaskAliases)
        {
            taskAliasesText = preferences.TaskAliases;
            taskAliases = CompanionTasks.ParseAliases(taskAliasesText);
        }
        return taskAliases;
    }

    // Shortcuts of the Start menu and the desktop (this user's and everybody's); read again every five minutes.
    private List<(string Name, string Path)> TaskShortcuts()
    {
        if (taskShortcuts != null && DateTime.UtcNow - taskShortcutsAt < TimeSpan.FromMinutes(5)) return taskShortcuts;
        var found = new List<(string, string)>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length == 0 || !Directory.Exists(path)) continue;
                foreach (string pattern in new[] { "*.lnk", "*.appref-ms" })
                    foreach (string file in Directory.EnumerateFiles(path, pattern, options))
                        if (found.Count < 3000) found.Add((System.IO.Path.GetFileNameWithoutExtension(file), file));
            }
            catch { }
        }
        taskShortcutsAt = DateTime.UtcNow;
        return taskShortcuts = found;
    }

    private static string? AppPath(string exe)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(exe, @"^[\w.\- ]{1,60}$")) return null;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                string? path = (key?.GetValue(null) as string)?.Trim().Trim('"');
                if (!string.IsNullOrEmpty(path) && File.Exists(Environment.ExpandEnvironmentVariables(path))) return Environment.ExpandEnvironmentVariables(path);
            }
            catch { }
        }
        return null;
    }

    private static string? WindowsProgram(string exe)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.System, Environment.SpecialFolder.Windows })
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(folder), exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // What a spoken name starts: the owner's own alias, a built-in name, a shortcut, a program registered with Windows.
    // "browser", a web address, a "uri:" protocol or a path; null when she knows nothing by that name.
    private string? ResolveTaskApp(string name)
    {
        string wanted = CompanionTasks.Normal(name);
        if (wanted.Length == 0) return null;
        if (TaskAliases().TryGetValue(wanted, out var alias)) return alias;
        string? builtIn = CompanionTasks.BuiltInTarget(name);
        if (builtIn == "browser" || builtIn?.StartsWith("uri:", StringComparison.Ordinal) == true) return builtIn;
        if (builtIn?.StartsWith("exe:", StringComparison.Ordinal) == true && WindowsProgram(builtIn[4..]) is { } system) return system;
        if (builtIn?.StartsWith("path:", StringComparison.Ordinal) == true && AppPath(builtIn[5..]) is { } registered) return registered;
        return CompanionTasks.MatchShortcut(TaskShortcuts(), name) ?? AppPath(name.Trim());
    }

    private bool TaskKnownApp(string name) => ResolveTaskApp(name) != null;

    // ---------------------------------------------------------------- opening
    private static string? ShellOpen(string target, string? folder = null)
    {
        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = true };
            if (folder != null && Directory.Exists(folder)) start.WorkingDirectory = folder;
            using var process = Process.Start(start);
            return null;
        }
        catch (Exception ex) { return "打不开（" + ex.Message + "）"; }
    }

    private static string? OpenTaskPath(string path)
    {
        if (Directory.Exists(path)) return ShellOpen(path);
        if (!File.Exists(path)) return "没有这个文件或文件夹";
        if (CompanionTasks.BlockedFile(path)) return "这类文件（脚本、安装包、注册表文件）鱼鱼不替你运行";
        return ShellOpen(path, System.IO.Path.GetDirectoryName(path));
    }

    private string? OpenTaskTarget(string target)
    {
        string engine = preferences.TaskSearchEngine;
        if (target == "browser") return ShellOpen(CompanionTasks.EngineOf("", engine).Home);
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return CompanionTasks.NormalizeUrl(target) is { } url ? ShellOpen(url) : "这个网址不对";
        if (CompanionTasks.NormalizePath(target) is { } path) return OpenTaskPath(path);
        string? resolved = ResolveTaskApp(target);
        if (resolved == null)
            return $"没找到叫「{target}」的软件。可以在 设置 → 互动与关怀 →「帮我做事」里给它起个别名（名字=程序路径）";
        if (resolved == "browser") return ShellOpen(CompanionTasks.EngineOf("", engine).Home);
        if (resolved.StartsWith("uri:", StringComparison.Ordinal)) return ShellOpen(resolved[4..]);
        if (CompanionTasks.NormalizeUrl(resolved) is { } web && resolved.Contains("://")) return ShellOpen(web);
        return OpenTaskPath(resolved);
    }

    // The window that came to the front after something was opened (the program may need a moment); zero when none did.
    private async Task<IntPtr> NewForegroundAsync(IntPtr before, int seconds)
    {
        for (int i = 0; i < seconds * 10 && !companionClosed; i++)
        {
            await Task.Delay(100);
            var front = CompanionNative.GetForegroundWindow();
            if (front != IntPtr.Zero && front != before && CompanionNative.IsExternalForegroundCandidate(front))
            {
                await Task.Delay(600); // let it finish drawing and take the keyboard
                var settled = CompanionNative.GetForegroundWindow();
                return CompanionNative.IsExternalForegroundCandidate(settled) ? settled : front;
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- the keyboard
    private static async Task<bool> FocusIsPasswordAsync()
    {
        try
        {
            var look = Task.Run(() =>
            {
                try { return System.Windows.Automation.AutomationElement.FocusedElement is { } focused && focused.Current.IsPassword; }
                catch { return false; }
            });
            return await Task.WhenAny(look, Task.Delay(1500)) == look && look.Result;
        }
        catch { return false; }
    }

    // Brings the window the keys are meant for to the front and says why not, when she must not or cannot type there.
    private async Task<(IntPtr Window, string? Error)> TaskInputWindowAsync(TaskRun run)
    {
        if (!preferences.TaskInput) return (IntPtr.Zero, "代你打字和按键在设置里关着（设置 → 互动与关怀 →「帮我做事」）");
        if (GameActive) return (IntPtr.Zero, "游戏开着的时候鱼鱼不碰键盘");
        if (run.Lost) return (IntPtr.Zero, "刚打开的软件一直没出现在最前面，鱼鱼不敢乱打字。等它打开后点一下它，再说一次要输入什么");
        var window = run.Window != IntPtr.Zero && CompanionTaskNative.IsWindow(run.Window) ? run.Window : lastExternalForeground;
        if (window == IntPtr.Zero || !CompanionTaskNative.IsWindow(window) || CompanionTaskNative.IsOwn(window) || !CompanionNative.IsExternalForegroundCandidate(window))
            return (IntPtr.Zero, "不知道要在哪个窗口里操作——先点一下那个窗口，再回来告诉鱼鱼");
        if (CompanionTasks.IsCommandLine(CompanionTaskNative.ProcessName(window))) return (IntPtr.Zero, "那是命令行窗口，鱼鱼不往里面打字");
        if (CompanionNative.GetForegroundWindow() != window)
        {
            if (CompanionTaskNative.IsIconic(window)) CompanionTaskNative.ShowWindow(window, 9);
            CompanionTaskNative.SetForegroundWindow(window);
            for (int i = 0; i < 10 && CompanionNative.GetForegroundWindow() != window; i++) await Task.Delay(60);
            if (CompanionNative.GetForegroundWindow() != window) return (IntPtr.Zero, "切不到那个窗口（它可能是用管理员权限打开的）");
            await Task.Delay(200);
        }
        if (await FocusIsPasswordAsync()) return (IntPtr.Zero, "光标在密码框里，鱼鱼不替你输密码");
        run.Window = window;
        return (window, null);
    }

    private async Task<string?> TypeTaskTextAsync(TaskRun run, string text)
    {
        var (window, error) = await TaskInputWindowAsync(run);
        if (error != null) return error;
        for (int at = 0; at < text.Length; at += 16)
        {
            // somebody (or a dialog) took the keyboard away: stop rather than type into the wrong place
            if (CompanionNative.GetForegroundWindow() != window) return "打到一半窗口被切走了，鱼鱼停下来了";
            if (!CompanionTaskNative.Type(text.Substring(at, Math.Min(16, text.Length - at)))) return "Windows 没让鱼鱼打字（那个窗口可能是用管理员权限打开的）";
            await Task.Delay(15);
        }
        return null;
    }

    private async Task<string?> PressTaskKeysAsync(TaskRun run, string combo, bool needsWindow = true)
    {
        if (!preferences.TaskInput) return "代你打字和按键在设置里关着（设置 → 互动与关怀 →「帮我做事」）";
        if (GameActive) return "游戏开着的时候鱼鱼不碰键盘";
        if (needsWindow)
        {
            var (_, error) = await TaskInputWindowAsync(run);
            if (error != null) return error;
        }
        var keys = combo.Split('+').Select(key => CompanionTasks.Keys[key]).ToList();
        if (!CompanionTaskNative.Press(keys)) return "Windows 没让鱼鱼按键";
        await Task.Delay(120);
        return null;
    }

    // ---------------------------------------------------------------- one step
    private async Task<string?> RunTaskStepAsync(TaskStep step, TaskRun run)
    {
        switch (step.Kind)
        {
            case TaskKind.Open:
            {
                var before = CompanionNative.GetForegroundWindow();
                if (OpenTaskTarget(step.Target) is { } error) return error;
                run.Window = await NewForegroundAsync(before, run.WaitSeconds);
                run.Lost = run.Window == IntPtr.Zero;
                return null;
            }
            case TaskKind.Search:
            {
                var before = CompanionNative.GetForegroundWindow();
                if (ShellOpen(CompanionTasks.SearchUrl(step.Target, preferences.TaskSearchEngine, step.Text)) is { } error) return error;
                run.Window = await NewForegroundAsync(before, run.WaitSeconds);
                run.Lost = run.Window == IntPtr.Zero;
                return null;
            }
            case TaskKind.Type:
                return await TypeTaskTextAsync(run, step.Text);
            case TaskKind.Key:
                // Win+… and Alt+Tab act on the desktop, not on one window
                return await PressTaskKeysAsync(run, step.Target, needsWindow: !step.Target.Contains("win") && step.Target != "alt+tab");
            case TaskKind.Volume:
            {
                int now = CompanionTaskNative.Volume(step.Number, step.Target);
                if (now >= 0) { run.Note = step.Target is "mute" ? "已经静音" : $"现在音量 {now}%"; return null; }
                // no access to the audio device: the volume keys still work (2 points a press)
                ushort key = step.Target switch { "mute" or "unmute" => (ushort)0xAD, "down" => (ushort)0xAE, _ => (ushort)0xAF };
                bool pressed = true;
                if (double.IsNaN(step.Number))
                    for (int i = 0; i < (step.Target is "up" or "down" ? 5 : 1); i++) pressed &= CompanionTaskNative.Press(new[] { key });
                else
                {
                    for (int i = 0; i < 50; i++) pressed &= CompanionTaskNative.Press(new ushort[] { 0xAE });
                    for (int i = 0; i < (int)Math.Round(step.Number / 2); i++) pressed &= CompanionTaskNative.Press(new ushort[] { 0xAF });
                }
                return pressed ? null : "调不了音量";
            }
            case TaskKind.Window:
            {
                if (step.Target == "desktop") return await PressTaskKeysAsync(run, "win+d", false);
                if (step.Target == "switch") return await PressTaskKeysAsync(run, "alt+tab", false);
                if (run.Lost) return "刚打开的软件一直没出现在最前面，鱼鱼不知道该动哪个窗口";
                var window = run.Window != IntPtr.Zero && CompanionTaskNative.IsWindow(run.Window) ? run.Window : lastExternalForeground;
                if (window == IntPtr.Zero || !CompanionTaskNative.IsWindow(window) || CompanionTaskNative.IsOwn(window) || !CompanionNative.IsExternalForegroundCandidate(window))
                    return "不知道是哪个窗口——先点一下那个窗口，再回来告诉鱼鱼";
                if (step.Target == "close") return CompanionTaskNative.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero) ? null : "关不掉那个窗口";
                CompanionTaskNative.ShowWindow(window, step.Target switch { "minimize" => 6, "maximize" => 3, _ => 9 });
                return null;
            }
            case TaskKind.Screenshot:
                try
                {
                    var area = System.Windows.Forms.SystemInformation.VirtualScreen;
                    using var bitmap = new System.Drawing.Bitmap(area.Width, area.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "鱼鱼截图");
                    Directory.CreateDirectory(folder);
                    string file = System.IO.Path.Combine(folder, $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    run.Note = "存在 " + file;
                    return null;
                }
                catch (Exception ex) { return "截图没存成（" + ex.Message + "）"; }
            case TaskKind.Lock:
                return CompanionTaskNative.LockWorkStation() ? null : "锁不了屏";
            case TaskKind.Settings:
                if (step.Target == "pet") { OpenCompanionSettings(); return null; }
                return CompanionTasks.SettingsUri(step.Target) is { } uri ? ShellOpen(uri) : "没有这个设置页面";
            case TaskKind.Shutdown:
            case TaskKind.CancelShutdown:
                try
                {
                    string exe = WindowsProgram("shutdown.exe") ?? "shutdown.exe";
                    string arguments = step.Kind == TaskKind.Shutdown ? "/s /t " + ((int)Math.Clamp(step.Number, 60, 86400)).ToString(CultureInfo.InvariantCulture) : "/a";
                    using var process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true });
                    if (process != null && process.WaitForExit(5000) && process.ExitCode != 0)
                        return step.Kind == TaskKind.Shutdown ? "Windows 没有接受（可能已经定了一个关机时间，先说「取消关机」）" : "现在没有定时关机可以取消";
                    return null;
                }
                catch (Exception ex) { return "没做成（" + ex.Message + "）"; }
            case TaskKind.Clip:
                try { Clipboard.SetText(step.Text); return null; }
                catch (Exception ex) { return "剪贴板被别的程序占着（" + ex.Message + "）"; }
            default:
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(step.Number, 1, 30)));
                return null;
        }
    }

    // ---------------------------------------------------------------- a whole command
    private async Task RunTaskAsync(string message, List<TaskStep> steps)
    {
        if (taskRunning) { Say("鱼鱼手上的事还没做完，等一下下～"); return; }
        taskRunning = true;
        // a program that is to be typed into gets up to 20 seconds to appear; otherwise 3 are enough to keep the order
        var run = new TaskRun { WaitSeconds = steps.Any(CompanionTasks.UsesInput) ? 20 : 3 };
        string? failure = null;
        int done = 0;
        try
        {
            if (steps.Count > 1 || steps.Any(CompanionTasks.UsesInput)) Say("鱼鱼这就去做～", "工作状态-忙碌点按");
            foreach (var step in steps)
            {
                if (companionClosed) return;
                string? error = await RunTaskStepAsync(step, run);
                if (error != null)
                {
                    failure = (steps.Count > 1 ? $"第 {done + 1} 步（{CompanionTasks.Describe(step, preferences.TaskSearchEngine)}）没做成：" : "没做成：") + error;
                    break;
                }
                done++;
            }
        }
        catch (Exception ex) { failure = "出了点问题：" + ex.Message; }
        finally { taskRunning = false; }
        if (companionClosed) return;
        string reply = failure == null
            ? (steps.Count == 1 ? "好啦：" + CompanionTasks.Describe(steps[0], preferences.TaskSearchEngine) : $"{steps.Count} 步都做好啦～") + (run.Note != null ? "（" + run.Note + "）" : "")
            : failure + (done > 0 ? $"（前 {done} 步已经做了）" : "");
        ChatReply(message, reply, failure == null ? "工作状态-雀跃庆祝" : "工作状态-垂头叹气冒汗", remember: false);
        PetEvent("task.done", failure == null ? "成功" : "失败", said: reply);
    }

    // Chat: a command is carried out (or shown first); anything else goes on to the ordinary chat.
    private async Task<bool> TryTaskChatAsync(string message, TextBlock status)
    {
        if (!preferences.TaskEnabled) return false;
        if (pendingTask is { } waiting)
        {
            pendingTask = null;
            if (DateTime.UtcNow <= waiting.Expires && CompanionTasks.Yes.IsMatch(message.Trim()))
            {
                ClearBubbleActions();
                await RunTaskAsync(waiting.Message, waiting.Steps);
                return true;
            }
            if (DateTime.UtcNow <= waiting.Expires && CompanionTasks.No.IsMatch(message.Trim()))
            {
                ChatReply(message, "好哒，不做啦～", remember: false);
                return true;
            }
        }
        var parse = CompanionTasks.Parse(message, TaskKnownApp);
        var steps = parse.Steps.Count > 0 && parse.Unknown == null ? parse.Steps.ToList() : null;
        bool fromModel = false;
        if (steps == null && preferences.TaskUseModel && (parse.Unknown != null || CompanionTasks.LooksLikeTask(message)))
        {
            status.Text = "鱼鱼在想该怎么做……";
            var planned = await PlanTaskAsync(message);
            if (companionClosed) return true;
            if (planned is { Count: > 0 }) { steps = planned; fromModel = true; }
        }
        if (steps == null)
        {
            if (parse.Unknown == null) return false;
            ChatReply(message, $"这句里的「{parse.Unknown}」鱼鱼没听懂，所以什么都还没做。可以换个说法，比如「打开记事本，然后输入：……」。", remember: false);
            return true;
        }
        if (!preferences.TaskInput && steps.Any(CompanionTasks.UsesInput))
        {
            ChatReply(message, "这件事要代你打字或按键，这个开关在设置里关着（设置 → 互动与关怀 →「帮我做事」）。", remember: false);
            return true;
        }
        if (!CompanionTasks.NeedsConfirm(steps, preferences.TaskConfirm, fromModel))
        {
            await RunTaskAsync(message, steps);
            return true;
        }
        pendingTask = new PendingTask(message, steps, DateTime.UtcNow.AddMinutes(2));
        string text = (fromModel ? "鱼鱼是这样理解的，对的话点「执行」：\n" : "鱼鱼要做这些，可以吗？\n") + CompanionTasks.Describe(steps, preferences.TaskSearchEngine);
        // keys or a window command before anything is opened go to the window that was in use before the chat box: say which
        var firstUse = steps.FirstOrDefault(step => step.Kind is TaskKind.Open or TaskKind.Search || CompanionTasks.UsesInput(step)
            || step.Kind == TaskKind.Window && step.Target is not ("desktop" or "switch"));
        if (firstUse != null && firstUse.Kind is not (TaskKind.Open or TaskKind.Search))
        {
            var title = new System.Text.StringBuilder(200);
            if (lastExternalForeground != IntPtr.Zero && CompanionTaskNative.IsWindow(lastExternalForeground)) CompanionNative.GetWindowText(lastExternalForeground, title, title.Capacity);
            text += title.Length > 0 ? $"\n（在你刚才用的窗口「{(title.Length > 30 ? title.ToString(0, 30) + "…" : title.ToString())}」里）" : "\n（在你刚才用的窗口里）";
        }
        SayWithActions(text, "工作状态-思考冒泡",
            ("执行", () => { var task = pendingTask; pendingTask = null; if (task != null) _ = RunTaskAsync(task.Message, task.Steps); }),
            ("取消", () => { pendingTask = null; Say("好哒，不做啦～"); }));
        LogChat(message, text, "鱼鱼");
        return true;
    }

    // Wordings the rules do not know: the chat model puts the sentence into the same steps (联网模型 first, else the local one).
    private async Task<List<TaskStep>?> PlanTaskAsync(string message)
    {
        var messages = new[] { new CompanionBrain.ChatMessage("system", CompanionTasks.PlannerPrompt), new CompanionBrain.ChatMessage("user", message.Trim()) };
        string? answer = NetChatReady ? await NetCompleteAsync(messages, 500) : null;
        if (string.IsNullOrWhiteSpace(answer) && preferences.ModelEnabled && brain.ModelAvailable && brain.CanRunOnCurrentPower)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed) await Task.Delay(150);
            if (!brain.Busy && !companionClosed) answer = await brain.GeneratePlain(messages, 400);
        }
        return string.IsNullOrWhiteSpace(answer) ? null : CompanionTasks.ParsePlan(answer);
    }

    // One answer from the network model, not streamed and not shown; null when none of its models answered in time.
    private async Task<string?> NetCompleteAsync(CompanionBrain.ChatMessage[] messages, int maxTokens)
    {
        var provider = CurrentProvider;
        string? key = NetKey(provider);
        if (key == null) return null;
        int tried = 0;
        foreach (string model in NetChatModels(provider))
        {
            if (netModelSkipUntil.TryGetValue(model, out var until) && DateTime.UtcNow < until) continue;
            if (++tried > 2) break;
            try
            {
                object payload = provider == DeepSeekProvider
                    ? new { model, messages, stream = false, max_tokens = maxTokens, temperature = 0.2, thinking = new { type = "disabled" } }
                    : new { model, messages, stream = false, max_tokens = maxTokens, temperature = 0.2, reasoning = new { effort = "none" } };
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = NetRequest(provider, HttpMethod.Post, "/chat/completions", key);
                request.Content = JsonContent.Create(payload);
                using var response = await netChatWeb.SendAsync(request, limit.Token);
                if (!response.IsSuccessStatusCode) continue;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
                if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var reply) && reply.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                    return text;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { }
        }
        return null;
    }

    // Right-click 「帮我做事…」: the chat box, with a few examples of what can be said.
    private void ShowTaskHelp()
    {
        if (!TryOpenChat()) { Say(preferences.ChatEnabled ? "鱼鱼现在不方便～" : "聊天功能在设置里关掉了，帮我做事要在聊天框里说哦～"); return; }
        Say(preferences.TaskEnabled
            ? "在聊天框里告诉鱼鱼要做什么，比如：\n「打开记事本，然后输入：今天的待办」\n「用百度搜一下明天的天气」\n「把音量调到 30」「截个图」「30 分钟后关机」"
            : "「帮我做事」在设置里关着（设置 → 互动与关怀 →「帮我做事」），打开后就能在聊天框里让鱼鱼操作电脑啦～");
    }

    // ---------------------------------------------------------------- settings
    private void AddTaskSettings(StackPanel page, Action<string> section, Action<string> note, Func<string, bool, Action<bool>, CheckBox> toggle)
    {
        section("帮我做事（操作电脑上的软件）");
        toggle("在聊天框里说一句话，鱼鱼替你打开软件、文件、网页，搜索，调音量，截图，锁屏，定时关机等", preferences.TaskEnabled, v => preferences.TaskEnabled = v);
        toggle("允许她代你打字和按键（在刚打开的软件或你刚才用的窗口里；密码框、命令行窗口和游戏里不打）", preferences.TaskInput, v => preferences.TaskInput = v);
        toggle("规则听不懂的说法，交给聊天模型拆成步骤（模型拆出来的步骤一定先给你看，点「执行」才做）", preferences.TaskUseModel, v => preferences.TaskUseModel = v);
        StackPanel Row() { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) }; page.Children.Add(row); return row; }
        ComboBox Choice(IReadOnlyList<string> names, int selected, Action<int> change)
        {
            var combo = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
            foreach (string name in names) combo.Items.Add(name);
            combo.SelectedIndex = Math.Clamp(selected, 0, names.Count - 1);
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { change(combo.SelectedIndex); SaveCompanion(); } };
            return combo;
        }
        var confirm = Row();
        confirm.Children.Add(new TextBlock { Text = "动手之前", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        confirm.Children.Add(Choice(new[] { "有风险的先问我（打字按键、关窗口）", "每次都先问我", "不问，直接做（关机仍然会问）" }, preferences.TaskConfirm, i => preferences.TaskConfirm = i));
        var engine = Row();
        engine.Children.Add(new TextBlock { Text = "搜索用", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        engine.Children.Add(Choice(CompanionTasks.Engines.Select(e => e.Name).ToList(), Array.FindIndex(CompanionTasks.Engines, e => e.Id == preferences.TaskSearchEngine),
            i => preferences.TaskSearchEngine = CompanionTasks.Engines[i].Id));
        page.Children.Add(new TextBlock { Text = "软件别名（每行一个：名字=程序、文件夹的路径或网址），找不到某个软件时在这里告诉鱼鱼：", FontSize = 12, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap });
        var aliases = new TextBox { Text = preferences.TaskAliases.Replace("\n", "\r\n"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 64, MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 2, 4, 2), FontSize = 12 };
        aliases.LostFocus += (_, _) =>
        {
            string text = string.Join("\n", aliases.Text.Replace("\r", "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Take(50));
            if (text.Length > 6000) text = text[..6000];
            if (text == preferences.TaskAliases) return;
            preferences.TaskAliases = text;
            SaveCompanion();
        };
        page.Children.Add(aliases);
        note("例子：「打开微信」「打开 D:\\资料」「打开 www.bing.com」「用百度搜一下明天的天气」「在记事本里写：今天的待办」「打开记事本，输入“第一行”，再按回车」"
            + "「按 Ctrl+S」「把音量调到 30」「静音」「显示桌面」「最小化当前窗口」「截个图」「锁屏」「打开蓝牙设置」「30 分钟后关机」「取消关机」。右键鱼鱼「帮我做事…」也能看到。"
            + "软件按名字在开始菜单和桌面的快捷方式里找。只有你在聊天框里说的话才会让她动手；她不运行命令行、脚本和安装包，不删除文件，不点软件里的按钮，不替你输密码和付款。"
            + "交给联网模型拆步骤时，这句话会像聊天一样发给所选的服务。");
    }
}
