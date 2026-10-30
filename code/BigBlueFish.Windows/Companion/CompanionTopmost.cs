using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace BigBlueFish.Windows;

// 置顶 / 不置顶: with "always on top" off she lives among the other windows. A touch or the tray
// icon brings her forward; reminders lift her above everything until their bubble is gone, and
// with 游戏陪玩 on she stays above the game while it is in front.
public partial class MainWindow
{
    private bool noticeRaised;

    private void SetAlwaysOnTop(bool on, bool announce)
    {
        preferences.AlwaysOnTop = on;
        noticeRaised = false;
        ApplyTopmost();
        RaiseCompanion();
        SaveCompanion();
        if (announce)
            ShowCompanionNotice(on ? "好的～鱼鱼会一直待在最前面陪着你！" : "好的～鱼鱼不挡着你啦，会待在窗口后面；有提醒时再跳出来～");
    }

    // Brings her to the front of the ordinary windows without taking the keyboard focus.
    private void RaiseCompanion()
    {
        if (Topmost) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            CompanionNative.SetWindowPos(handle, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x0013 /* NOSIZE | NOMOVE | NOACTIVATE */);
    }

    // A reminder must be seen: stay above everything until its bubble is gone.
    private void RaiseForNotice()
    {
        if (preferences.AlwaysOnTop) return;
        noticeRaised = true;
        Topmost = true;
    }

    // Every second (CareTick): settle on the band she should be in right now.
    private void ApplyTopmost()
    {
        if (noticeRaised && bubble.Visibility != Visibility.Visible) noticeRaised = false;
        bool wanted = preferences.AlwaysOnTop || noticeRaised || gameForeground && preferences.GameCompanion;
        if (Topmost != wanted) Topmost = wanted;
    }

    private void RaiseOnTouch(object sender, MouseButtonEventArgs e) => RaiseCompanion();
}
