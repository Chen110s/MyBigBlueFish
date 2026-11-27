using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace BigBlueFish.Windows;

// 键鼠陪伴 (from KuroCat's 大肥鱼): while it is on, her clips are hidden and paused and she sits behind a keyboard and a
// mouse drawn by CompanionKeyMouseScene, following the keyboard and the mouse. She stays where she is (no wandering,
// crawling, running, flinging or hiding at an edge); clicks, chat, the bubble and every other feature work as usual.
//
// Input is read by polling GetAsyncKeyState (as the Delta Force mimic does): which keys and mouse buttons are down and
// where the pointer is. No hook, nothing typed is read, kept or sent. When the focus is in a password field (Win32
// ES_PASSWORD, UI Automation IsPassword, or a PasswordBox of this program) she covers her eyes and the keycaps are
// hidden: no key is shown at all.
public partial class MainWindow
{
    private CompanionKeyMouseView? keyMouseView;
    private readonly Stopwatch keyMouseClock = Stopwatch.StartNew();
    private double keyMouseLast, keyMouseFrame;
    private readonly bool[] keyMouseDown = new bool[256];
    private bool keyMouseTried;
    private static readonly int[] MouseButtonKeys = [0x01, 0x02, 0x04]; // left, right, middle

    private bool KeyMouseActive => keyMouseView != null;
    private static string KeyMouseAssets => Path.Combine(AppContext.BaseDirectory, "assets", "dshpet", "kurocat");
    internal static bool KeyMouseAvailable => File.Exists(Path.Combine(KeyMouseAssets, "notail_idle.png"));

    private void ToggleKeyMouse()
    {
        if (KeyMouseActive) { LeaveKeyMouse(true); Say("键鼠陪伴关掉啦～鱼鱼回来啦！"); }
        else if (EnterKeyMouse(true)) Say("键鼠陪伴开啦～你敲键盘、动鼠标，鱼鱼都跟着你！");
    }

    private bool EnterKeyMouse(bool remember)
    {
        if (KeyMouseActive) return true;
        if (!companionReady || exitAnimation || companionClosed || videoImage == null) return false;
        var view = new CompanionKeyMouseView();
        if (!view.LoadSprites(KeyMouseAssets))
        {
            ShowCompanionNotice("键鼠陪伴缺少图片（assets\\dshpet\\kurocat），请重新运行 edge-crawl\\Build-EdgeCrawl.bat～");
            return false;
        }
        // out of anything that moves her about first
        if (coast != null) EndCoast(false);
        LeaveEdgeHide();
        if (Crawling) StopCrawlNow(false);
        StopTargetRun();
        EndTargetSelection();
        CancelMovement();
        view.Scene.SetBoard(preferences.KeyMouseBoard);
        view.Scene.ShowBubbles = preferences.KeyMouseBubbles;
        view.Scene.UseTail = preferences.KeyMouseTail;
        keyMouseView = view;
        if (SpriteHostElement() is { } host) host.Visibility = Visibility.Hidden;
        companionLayout.Children.Insert(Math.Min(1, companionLayout.Children.Count), view);
        Array.Clear(keyMouseDown, 0, keyMouseDown.Length);
        keyMouseLast = keyMouseClock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += KeyMouseFrame;
        StartPrivacyWatch();
        UpdateCompanionVisibility(); // pauses her clips
        if (remember) { preferences.KeyMouse = true; SaveCompanion(); }
        return true;
    }

    private void LeaveKeyMouse(bool remember)
    {
        if (keyMouseView is not { } view) return;
        CompositionTarget.Rendering -= KeyMouseFrame;
        StopPrivacyWatch();
        keyMouseView = null;
        companionLayout.Children.Remove(view);
        if (SpriteHostElement() is { } host) host.Visibility = Visibility.Visible;
        UpdateCompanionVisibility();
        if (remember) { preferences.KeyMouse = false; SaveCompanion(); }
        if (companionReady && !exitAnimation && !companionClosed) PlayIdle();
    }

    // The element that holds her clip (the sprite host around videoImage).
    private UIElement? SpriteHostElement()
    {
        if (videoImage == null) return null;
        FrameworkElement element = videoImage;
        while (element.Parent is FrameworkElement parent && parent != companionLayout) element = parent;
        return element;
    }

    // CareTick: switch it back on after a restart once she has greeted.
    private void KeyMouseTick()
    {
        if (keyMouseTried || !preferences.KeyMouse || KeyMouseActive || !companionReady || animationPhase == "Startup" || exitAnimation) return;
        keyMouseTried = true;
        EnterKeyMouse(false);
    }

    // ---------------------------------------------------------------- every frame
    private void KeyMouseFrame(object? sender, EventArgs e)
    {
        if (keyMouseView is not { } view || companionClosed) return;
        double now = keyMouseClock.Elapsed.TotalMilliseconds;
        double dt = Math.Clamp((now - keyMouseLast) / 1000, 0, .1);
        keyMouseLast = now;
        if (!IsVisible || PresentationSource.FromVisual(this) == null) return;
        try { KeyMouseStep(view, now, dt); }
        catch (Exception error) { view.Failure ??= error; }
        if (view.Failure is { } failure)
        {
            // never let the scene take her down: log it and go back to her own animations
            LogCompanionError($"键鼠陪伴出错，已关闭：{failure.GetType().Name}：{failure.Message}");
            LeaveKeyMouse(true);
            ShowCompanionNotice("键鼠陪伴出了点问题，鱼鱼先回到平时的样子啦（详情见 data\\companion-errors.log）");
        }
    }

    private void KeyMouseStep(CompanionKeyMouseView view, double now, double dt)
    {
        var scene = view.Scene;

        // lay her over her own body, the keyboard inside the window
        var (sl, st, sw, sh) = scene.SpriteRect;
        var body = UprightVisibleBounds;
        double k = body.Height / sh;
        double ox = body.X + body.Width / 2 - (sl + sw / 2) * k, oy = body.Bottom - (st + sh) * k;
        double bottom = oy + scene.ContentBottom * k;
        if (bottom > ActualHeight - 2) oy -= bottom - (ActualHeight - 2);
        double left = ox + scene.ContentLeft * k;
        if (left < 2) ox += 2 - left;
        view.SceneScale = k; view.OffsetX = ox; view.OffsetY = oy;

        PollKeyMouse(scene, now, k, ox, oy);
        scene.Update(dt, now);
        view.Now = now;
        // 60 frames a second while something moves, 30 otherwise (breathing, blinking)
        keyMouseFrame++;
        if (scene.Busy(now) || keyMouseFrame % 2 == 0) view.InvalidateVisual();
    }

    private void PollKeyMouse(CompanionKeyMouseScene scene, double now, double k, double ox, double oy)
    {
        UpdatePrivacy(scene);
        // keys: the left / right variants of Shift, Ctrl and Alt rather than the shared codes; no mouse buttons here
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12 or 0xE5 or 0xE7) continue;
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down == keyMouseDown[vk]) continue;
            keyMouseDown[vk] = down;
            if (down)
            {
                if (vk == 0x09) AskPrivacy(); // Tab moves the focus: look at the new field
                scene.KeyDown(vk, now);
            }
            else scene.KeyUp(vk, now);
        }
        for (int b = 0; b < 3; b++)
        {
            int vk = MouseButtonKeys[b];
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down == keyMouseDown[vk]) continue;
            keyMouseDown[vk] = down;
            if (down) AskPrivacy(); // a click may have put the caret into a password field
            scene.MouseButton(b, down, now);
        }
        CompanionNative.GetCursorPos(out var cursor);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
        double nx = (cursor.X - screen.Left) / (double)Math.Max(1, screen.Width), ny = (cursor.Y - screen.Top) / (double)Math.Max(1, screen.Height);
        var local = PointFromScreen(new Point(cursor.X, cursor.Y));
        scene.MouseMove(Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1), ((local.X - ox) / k, (local.Y - oy) / k), now);
    }

    // ---------------------------------------------------------------- password fields
    private Thread? privacyThread;
    private volatile bool privacyRunning, privacyResult;
    private IntPtr privacyForeground, privacyFocus; // written by the UI thread, read by the watcher
    // Each change that may have moved the caret (another window or field, a click, Tab) gets a new number; until the
    // watcher has looked at the field for that number the keys are not shown (she still bobs along).
    private int privacyAsked, privacyAnswered, privacyGeneration;
    private readonly AutoResetEvent privacyWake = new(false);

    private void StartPrivacyWatch()
    {
        if (privacyThread != null) return;
        privacyRunning = true;
        privacyResult = false;
        Interlocked.Increment(ref privacyAsked);
        int generation = ++privacyGeneration; // a watcher of an earlier start stops at its next look
        privacyThread = new Thread(() => PrivacyLoop(generation)) { IsBackground = true, Name = "键鼠陪伴 · 密码框", Priority = ThreadPriority.BelowNormal };
        privacyThread.SetApartmentState(ApartmentState.MTA);
        privacyThread.Start();
    }

    private void StopPrivacyWatch()
    {
        privacyRunning = false;
        privacyWake.Set();
        privacyThread = null;
    }

    private void AskPrivacy() { Interlocked.Increment(ref privacyAsked); privacyWake.Set(); }

    // UI thread, every frame: our own windows are looked at here (a PasswordBox, e.g. the API key), others by the watcher.
    private void UpdatePrivacy(CompanionKeyMouseScene scene)
    {
        var foreground = CompanionNative.GetForegroundWindow();
        CompanionNative.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId) { scene.SetPrivacy(Keyboard.FocusedElement is PasswordBox, false); return; }
        var focus = FocusedWindow(foreground);
        if (foreground != privacyForeground || focus != privacyFocus)
        {
            privacyForeground = foreground; privacyFocus = focus;
            privacyResult = false;
            AskPrivacy();
        }
        scene.SetPrivacy(privacyResult, Volatile.Read(ref privacyAnswered) != Volatile.Read(ref privacyAsked));
    }

    private void PrivacyLoop(int generation)
    {
        while (privacyRunning && generation == Volatile.Read(ref privacyGeneration))
        {
            int asked = Volatile.Read(ref privacyAsked);
            bool result;
            try
            {
                var foreground = privacyForeground;
                CompanionNative.GetWindowThreadProcessId(foreground, out uint pid);
                result = foreground != IntPtr.Zero && pid != (uint)Environment.ProcessId && FocusedIsPassword(foreground);
            }
            catch { result = false; }
            privacyResult = result;
            Volatile.Write(ref privacyAnswered, asked);
            if (Volatile.Read(ref privacyAsked) == asked) privacyWake.WaitOne(300); // asked again meanwhile: look again now
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    private static IntPtr FocusedWindow(IntPtr foreground)
    {
        uint thread = CompanionNative.GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    private static bool FocusedIsPassword(IntPtr foreground)
    {
        var focus = FocusedWindow(foreground);
        if (focus != IntPtr.Zero)
        {
            // a classic edit box with ES_PASSWORD (and not multi-line)
            var name = new StringBuilder(128);
            CompanionNative.GetClassName(focus, name, name.Capacity);
            long style = CompanionNative.GetStyle(focus, -16).ToInt64();
            if (name.ToString().Contains("edit", StringComparison.OrdinalIgnoreCase) && (style & 0x20) != 0 && (style & 0x4) == 0) return true;
        }
        // everything else (browsers, WPF, UWP …): UI Automation
        var focused = System.Windows.Automation.AutomationElement.FocusedElement;
        if (focused == null) return false;
        var current = focused.Current;
        if (!current.IsPassword) return false;
        var type = current.ControlType;
        return type == System.Windows.Automation.ControlType.Edit || type == System.Windows.Automation.ControlType.Custom
            || type == System.Windows.Automation.ControlType.Text;
    }

    // ---------------------------------------------------------------- settings (陪伴与外观)
    private void AddKeyMouseSettings(Panel page)
    {
        page.Children.Add(new TextBlock { Text = "键鼠陪伴（来自 KuroCat）", FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(0, 18, 0, 6) });
        var muted = Brushes.SlateGray;
        CheckBox Toggle(string label, bool value, Action<bool> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            box.Click += (_, _) => { change(box.IsChecked == true); SaveCompanion(); };
            page.Children.Add(box);
            return box;
        }
        var on = Toggle("键鼠陪伴：鱼鱼坐到键盘和鼠标后面，跟着你的键盘和鼠标动（右键菜单也能开关）", KeyMouseActive, v =>
        {
            if (v && !KeyMouseActive && !EnterKeyMouse(true)) { preferences.KeyMouse = false; }
            else if (!v && KeyMouseActive) LeaveKeyMouse(true);
        });
        on.IsEnabled = KeyMouseAvailable || KeyMouseActive;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = "键盘", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var board = new ComboBox { MinWidth = 160, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(board, "键鼠陪伴的键盘");
        board.Items.Add("65% 紧凑键盘");
        board.Items.Add("108 键全尺寸（带小键盘）");
        board.SelectedIndex = preferences.KeyMouseBoard == "108" ? 1 : 0;
        board.SelectionChanged += (_, _) =>
        {
            preferences.KeyMouseBoard = board.SelectedIndex == 1 ? "108" : "65";
            keyMouseView?.Scene.SetBoard(preferences.KeyMouseBoard);
            SaveCompanion();
        };
        row.Children.Add(board);
        page.Children.Add(row);
        Toggle("按键气泡：按下的键帽从键盘上飘起来，点鼠标时冒出 LMB / RMB 小角标", preferences.KeyMouseBubbles, v =>
        {
            preferences.KeyMouseBubbles = v;
            if (keyMouseView != null) keyMouseView.Scene.ShowBubbles = v;
        });
        Toggle("尾巴帮忙：两只手忙不过来时尾巴拍键，打完一串字尾巴拍回车", preferences.KeyMouseTail, v =>
        {
            preferences.KeyMouseTail = v;
            if (keyMouseView != null) keyMouseView.Scene.UseTail = v;
        });
        page.Children.Add(new TextBlock
        {
            Text = (KeyMouseAvailable ? "" : "缺少图片：重新运行 edge-crawl\\Build-EdgeCrawl.bat 就会装上。")
                + "她只看按了哪个键、鼠标在哪、按了哪个键（不读打的字，不保存，不上传）。输密码时她会闭上眼睛用手捂住，键帽也遮起来。"
                + "开着时她待在原地不走动、不爬行，甩她、拖到屏幕边也不会飞走或藏起来。以管理员身份运行的软件里打字，她可能看不到。",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });
    }
}
