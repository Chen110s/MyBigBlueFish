using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// Behaviour, after luweiyabo/dsh-whale-pet: three activity levels for her own actions, chat-state
// awareness (listening while you type, thinking while the local model works, startled on an error)
// and one priority arbitration that these states and the owner's trigger rules share.
public partial class MainWindow
{
    internal static readonly string[] ActivityLabels = ["安静（只在原地演）", "均衡", "活泼（动得更勤、会跑来跑去）"];
    private double ActivityIntervalFactor => preferences.ActivityLevel switch { 0 => 1.5, 2 => .6, _ => 1 };

    // A random action for the activity level: 安静 never picks a walking clip.
    private CompanionAnimationRule? ChooseActivityEvent()
    {
        var context = AnimationContext();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var rule = animationPolicy.Choose("Event", context, companionRandom);
                if (preferences.ActivityLevel != 0 || rule.MoveKind == null) return rule;
            }
        }
        catch (InvalidOperationException) { } // nothing in the pool can play right now
        return null;
    }

    // ---------------------------------------------------------------- what she is "about" right now
    private sealed record Intent(string Name, int Priority, string Clip, string? Fallback);
    private static readonly Intent ListenIntent = new("listen", 30, "侧耳倾听", "东张西望");
    private static readonly Intent ThinkIntent = new("think", 40, "深度思考碎碎念", null);
    private static readonly Intent ErrorIntent = new("error", 60, "被吓一跳", null);

    private Intent? currentIntent, pendingIntent;
    private bool intentPending;
    private DateTime pendingIntentAt, chatTypingUntil = DateTime.MinValue, chatErrorUntil = DateTime.MinValue, nextIntentCheck;
    private int playingPriority; // priority of the Intent / Rule clip on screen

    private void NoteChatTyping() => chatTypingUntil = DateTime.UtcNow.AddSeconds(2.5);

    private void NoteChatError()
    {
        chatErrorUntil = DateTime.UtcNow.AddSeconds(3);
        PetEvent("chat.error");
    }

    private Intent? WantedIntent(DateTime now)
    {
        if (!preferences.ChatSensing) return null;
        if (now < chatErrorUntil) return ErrorIntent;
        if (loadingIndicator?.Visibility == Visibility.Visible) return ThinkIntent;
        if (chatWindow != null && now < chatTypingUntil) return ListenIntent;
        if (deepSeekWindow?.IsActive == true && CompanionNative.IdleSeconds < 2) return ListenIntent; // you are busy in DeepSeek 网页版
        return null;
    }

    // CompanionTick: a higher state wins after 0.3 s, a fading one lingers 1.2 s so quick changes do not flicker.
    private void UpdateIntent(DateTime now)
    {
        if (now < nextIntentCheck) return;
        nextIntentCheck = now.AddMilliseconds(120);
        var wanted = WantedIntent(now);
        if (wanted == currentIntent) { intentPending = false; return; }
        if (!intentPending || wanted != pendingIntent) { pendingIntent = wanted; pendingIntentAt = now; intentPending = true; return; }
        double wait = (wanted?.Priority ?? 0) > (currentIntent?.Priority ?? 0) ? .3 : 1.2;
        if ((now - pendingIntentAt).TotalSeconds < wait) return;
        currentIntent = wanted;
        intentPending = false;
        if (wanted != null) PlayIntent(wanted);
    }

    private bool PlayIntent(Intent intent)
    {
        if (!ActionAllowed(intent.Priority)) return false;
        var rule = ClipOr(intent.Clip, intent.Fallback);
        if (rule == null) return false;
        CancelMovement();
        PlayAnimation(rule, "Intent");
        playingPriority = intent.Priority;
        return true;
    }

    // May an event (state or rule) of this priority take over the current animation? The owner's own
    // interactions, walking, crawling, mimicking, Agent (MCP) actions and previews always win; a running
    // Intent/Rule clip only yields to a higher priority. ownReaction: the clip on screen is the built-in
    // reaction to the very event the rule answers, which the rule may replace.
    private bool ActionAllowed(int priority, bool ownReaction = false)
    {
        if (!companionReady || companionClosed || exitAnimation || PuppetActive || dragging || coast != null || targetMove != null
            || Crawling || mimicActive || hiddenByUser || fullscreenHidden || movementRule != null) return false;
        if (animationPhase is "Drag" or "Fling" or "TargetRun" or "Arrive" or "Exit" or "External" or "Daily" or "Preview"
            || animationPhase.StartsWith("Mimic", StringComparison.Ordinal)) return false;
        if (animationPhase == "Startup" && !ownReaction) return false;
        if (animationPhase is "Click" or "Interact" && !ownReaction && (DateTime.UtcNow - animationStartedUtc).TotalSeconds < 2.5) return false;
        if (animationPhase is "Intent" or "Rule" && priority <= playingPriority) return false;
        return true;
    }

    // AnimationBoundary: an active state comes back after whatever interrupted it.
    private bool IntentBoundary()
    {
        var intent = currentIntent;
        return intent != null && WantedIntent(DateTime.UtcNow) == intent && PlayIntent(intent);
    }

    // AnimationFinished for the new phases.
    private bool BehaviourAnimationFinished()
    {
        if (TargetAnimationFinished()) return true;
        switch (animationPhase)
        {
            case "Fling":
                PlayIdle();
                return true;
            case "Intent":
                playingPriority = 0;
                if (currentIntent != null && WantedIntent(DateTime.UtcNow) == currentIntent && PlayIntent(currentIntent)) return true;
                AnimationBoundary();
                return true;
            case "Rule":
                playingPriority = 0;
                AnimationBoundary();
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- settings (陪伴与外观)
    private void AddInteractionSettings(Panel page)
    {
        var muted = Brushes.SlateGray;
        page.Children.Add(new TextBlock { Text = "互动手感与动作", FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(0, 18, 0, 6) });
        void Toggle(string label, bool value, Action<bool> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            box.Click += (_, _) => { change(box.IsChecked == true); SaveCompanion(); };
            page.Children.Add(box);
        }
        Toggle("悬停注视：鼠标在鱼鱼左边 / 右边时，她转向那一侧", preferences.HoverFacing, v => preferences.HoverFacing = v);
        Toggle("交互动效：光标靠近时轻轻倾斜，点她有 Q 弹回弹，拖动时挤压拉伸", preferences.InteractionFx, v => { preferences.InteractionFx = v; if (!v) tiltTarget = 0; });
        Toggle("点击分区：点头、身体、尾巴各有不同的回应", preferences.ClickZones, v => preferences.ClickZones = v);
        Toggle("快速甩出：她会变成球滑出去，碰到屏幕边反弹", preferences.FlingBall, v => preferences.FlingBall = v);
        Toggle("拖出屏幕时藏到边上，鼠标移到那条边时探出头来（点她就回来）", preferences.EdgeHide, v => { preferences.EdgeHide = v; if (!v) LeaveEdgeHide(); });
        Toggle("单击鱼鱼后再点屏幕任意位置，她跑过去挥手（5 秒内有效，右键取消；会接住这一下点击，不会点到下面的窗口）", preferences.ClickToMove, v => { preferences.ClickToMove = v; if (!v) EndTargetSelection(); });
        Toggle("聊天状态：你打字时她侧耳倾听，本地模型思考时深度思考，出错时被吓一跳", preferences.ChatSensing, v => preferences.ChatSensing = v);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        row.Children.Add(new TextBlock { Text = "活跃程度", Width = 80, VerticalAlignment = VerticalAlignment.Center });
        var level = new ComboBox { MinWidth = 260 };
        System.Windows.Automation.AutomationProperties.SetName(level, "活跃程度");
        foreach (var label in ActivityLabels) level.Items.Add(label);
        level.SelectedIndex = Math.Clamp(preferences.ActivityLevel, 0, 2);
        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedIndex < 0) return;
            preferences.ActivityLevel = level.SelectedIndex;
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds * ActivityIntervalFactor);
            SaveCompanion();
        };
        row.Children.Add(level);
        page.Children.Add(row);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        var library = BigBlueFish.Deployment.ModernUI.Button("动作库…", ShowActionLibrary);
        library.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(library);
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button($"触发规则（{triggerRules.Count}）…", ShowTriggerRules));
        page.Children.Add(buttons);
        page.Children.Add(new TextBlock { FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
            Text = "这些玩法参考了 luweiyabo/dsh-whale-pet。安静：只在原地演、不走动；均衡：和以前一样；活泼：动作更勤，还会自己跑来跑去。"
                + "Windows 关闭了「显示动画」时，倾斜、回弹和甩出滑行会自动停用。动作库里可以预览全部动作、调整它们在哪个动作池；"
                + "触发规则让她在你指定的事情发生时演指定的动作、说指定的话。" });
    }
}
