using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// Feeding: every eating clip she has, grouped into meals, feasts, snacks and festival food; a fullness
// meter that falls over time (she asks for food at meal times and refuses when stuffed); favourite foods
// and boredom with the same dish; drag a food onto her to feed her; and, when she has been hungry for a
// while, she sneaks a snack (click her then and she is caught). Buttons in her bubble come from here too.
public partial class MainWindow
{
    internal enum FoodKind { Meal, Feast, Snack, Fruit, Festival }

    internal sealed record Food(string Name, string Clip, FoodKind Kind, int Fill, int Taste, string? Festival, string[] Lines);

    // Taste: 2 favourite, 1 likes, 0 fine. Festival: when it is the right food for today.
    internal static readonly Food[] Foods =
    [
        new("大白饭", "吃白饭", FoodKind.Meal, 30, 2, null, ["哇！是香喷喷的大白饭！鱼鱼最最最喜欢了！", "大白饭！主人最懂鱼鱼了～开动啦！"]),
        new("油条豆浆", "吃早餐", FoodKind.Meal, 28, 1, null, ["油条蘸豆浆～早上吃这个最有精神啦！", "咔嚓咔嚓……豆浆好香！"]),
        new("一碗面", "吃午餐", FoodKind.Meal, 30, 1, null, ["热乎乎的面条～吸溜吸溜……", "好香的面！鱼鱼要把汤都喝光～"]),
        new("家常饭菜", "吃晚餐", FoodKind.Meal, 35, 1, null, ["红烧肉配米饭！还有番茄炒蛋～主人做的吗？", "一桌好菜……鱼鱼要吃两碗饭！"]),
        new("长寿面", "吃长寿面", FoodKind.Meal, 30, 1, null, ["长寿面要一口气吃完哦……祝主人长长久久！", "一根到底不能断～嘿嘿"]),
        new("饺子", "吃饺子", FoodKind.Meal, 30, 1, "SpringFestival", ["饺子！热乎乎的，主人真好～", "一口一个，鱼鱼吃得好幸福"]),
        new("火锅", "涮火锅", FoodKind.Feast, 45, 2, null, ["火锅！咕嘟咕嘟……毛肚七上八下！", "好辣好过瘾！主人也来一筷子～"]),
        new("大闸蟹", "吃大闸蟹", FoodKind.Feast, 40, 1, null, ["大闸蟹！蟹黄好多……鱼鱼要慢慢剥～", "秋天就是要吃蟹呀！"]),
        new("零食大礼包", "大口吃零食", FoodKind.Snack, 12, 1, null, ["一大堆零食！咔嚓咔嚓，好香～", "嘿嘿，零食是快乐的源泉！"]),
        new("冰淇淋", "吃冰淇淋融化", FoodKind.Snack, 10, 1, null, ["冰淇淋！凉凉甜甜的……啊要化了要化了！", "哇～冰淇淋！主人也来一口嘛"]),
        new("糖葫芦", "吃糖葫芦", FoodKind.Snack, 10, 2, null, ["糖葫芦！酸酸甜甜的，鱼鱼好开心～", "一颗、两颗……嘿嘿，舍不得吃完"]),
        new("西瓜", "吃西瓜", FoodKind.Fruit, 10, 1, null, ["大西瓜！咔嚓～好甜好甜！", "西瓜籽要吐掉哦……噗～"]),
        new("一把 Token", "吃Token", FoodKind.Snack, 5, 1, null, ["Token！鱼鱼的精神食粮～吃了脑袋转得更快！", "嗷呜～一口一个 Token！"]),
        new("月饼", "中秋赏月吃月饼", FoodKind.Festival, 20, 1, "MidAutumn", ["边赏月边吃月饼～主人中秋快乐！", "月亮好圆，月饼好甜……"]),
        new("汤圆", "吃汤圆", FoodKind.Festival, 20, 1, "Lantern", ["汤圆圆圆的～团团圆圆！", "芝麻馅的！烫烫烫……"]),
        new("粽子", "吃粽子", FoodKind.Festival, 25, 1, "DragonBoat", ["粽子！鱼鱼喜欢甜粽子……咸的也喜欢！", "剥开粽叶，好香的糯米～"]),
        new("年糕", "吃年糕", FoodKind.Festival, 25, 1, "SpringFestival", ["年糕年糕，年年高！", "软软糯糯的，好好吃～"]),
        new("腊八粥", "吃腊八粥", FoodKind.Festival, 25, 1, "Laba", ["腊八粥！暖乎乎的，喝完全身都热起来啦～", "好多种豆子和米……甜甜的！"]),
        new("青团", "吃青团", FoodKind.Festival, 20, 1, "Qingming", ["青团！青青的，有艾草的香味～", "豆沙馅的青团，软乎乎～"]),
        new("重阳糕", "吃重阳糕", FoodKind.Festival, 20, 1, "Chongyang", ["重阳糕～祝主人步步高！", "登高吃糕，鱼鱼也要长高高！"]),
    ];

    private static string KindLabel(FoodKind kind) => kind switch
    {
        FoodKind.Meal => "主食", FoodKind.Feast => "大餐", FoodKind.Snack or FoodKind.Fruit => "零食水果", _ => "节日美食"
    };

    private static readonly Dictionary<string, string> FestivalNames = new()
    {
        ["SpringFestival"] = "春节", ["Lantern"] = "元宵节", ["DragonBoat"] = "端午节", ["MidAutumn"] = "中秋节",
        ["Laba"] = "腊八节", ["Qingming"] = "清明", ["Chongyang"] = "重阳节"
    };

    // Is it (within a few days of) this festival today? Qingming and Chongyang are not in the animation calendar.
    private bool FestivalNow(string festival, DateTime local)
    {
        if (festival == "Qingming") return local.Month == 4 && local.Day >= 1 && local.Day <= 8;
        if (festival == "Chongyang")
        {
            try
            {
                var lunar = new ChineseLunisolarCalendar();
                for (int d = -3; d <= 3; d++)
                {
                    var day = local.Date.AddDays(d);
                    int month = lunar.GetMonth(day), leap = lunar.GetLeapMonth(lunar.GetYear(day));
                    if (leap != 0 && month == leap) continue;
                    if (leap != 0 && month > leap) month--;
                    if (month == 9 && lunar.GetDayOfMonth(day) == 9) return true;
                }
            }
            catch (ArgumentOutOfRangeException) { }
            return false;
        }
        return originalAnimationPolicy != null && originalAnimationPolicy.FestivalDistance(local.Date, festival) <= 3;
    }

    // ---------------------------------------------------------------- fullness & energy
    private const double FullnessPerHour = 8, EnergyPerHour = 15;

    private double Fullness()
    {
        var now = DateTime.UtcNow;
        double hours = Math.Clamp((now - bond.FullnessUtc).TotalHours, 0, 48);
        if (hours > 0.01) { bond.Fullness = Math.Max(0, bond.Fullness - hours * FullnessPerHour); bond.FullnessUtc = now; }
        return bond.Fullness;
    }

    private double Energy()
    {
        var now = DateTime.UtcNow;
        double hours = Math.Clamp((now - bond.EnergyUtc).TotalHours, 0, 48);
        if (hours > 0.01) { bond.Energy = Math.Min(100, bond.Energy + hours * EnergyPerHour); bond.EnergyUtc = now; }
        return bond.Energy;
    }

    private static string FullnessWord(double full) => full switch
    {
        < 15 => "饿扁了", < 35 => "有点饿", < 70 => "刚刚好", < 90 => "饱饱的", _ => "吃撑了"
    };

    private static string EnergyWord(double energy) => energy switch
    {
        < 20 => "玩累了", < 50 => "有点累", < 80 => "精神不错", _ => "元气满满"
    };

    // ---------------------------------------------------------------- feeding
    private void Feed(Food food)
    {
        if (!companionReady || exitAnimation || PuppetActive) return;
        var local = DateTime.Now;
        bond.RollDay(local);
        double full = Fullness();
        if (full + food.Fill * .4 > 100)
        {
            Say(Pick(new[] { "唔……鱼鱼已经吃得圆滚滚的啦，吃不下了……等会儿再喂好不好～", "饱饱的……肚子要撑破啦，鱼鱼先消化一下～" }), "哈欠连天");
            return;
        }
        bond.FoodToday.TryGetValue(food.Name, out int times);
        bond.FoodToday[food.Name] = times + 1;
        bond.Feeds++;
        bond.LastFedUtc = DateTime.UtcNow;
        bond.Fullness = Math.Min(100, full + food.Fill);
        bond.FullnessUtc = DateTime.UtcNow;
        bond.MealAskKey = MealKey(local) ?? bond.MealAskKey; // fed for this meal: no need to ask
        NoteInteraction();
        string season = CompanionAnimationPolicy.Season(local);
        bool festival = food.Festival != null && FestivalNow(food.Festival, local);
        bool bored = times >= 2;
        bool tooCold = food.Name is "冰淇淋" or "西瓜" && season == "Winter";
        double affection = (food.Kind == FoodKind.Feast ? 2.5 : food.Kind == FoodKind.Meal ? 2 : 1.5) * (food.Taste == 2 ? 1.5 : 1);
        string line;
        if (bored) { line = $"又是{food.Name}呀……今天已经吃了 {times} 次啦，鱼鱼想换个口味～（不过还是吃掉了）"; affection *= .3; }
        else if (festival) { line = $"今天是{FestivalNames[food.Festival!]}！{Pick(food.Lines)}"; affection *= 2; }
        else if (tooCold) { line = $"大冬天吃{food.Name}……好、好冰！鱼鱼的牙齿在打颤～"; affection *= .6; }
        else if (food.Name == "火锅" && season == "Summer") line = "大夏天吃火锅！又热又辣……但是好过瘾！";
        else if (food.Kind == FoodKind.Meal && NearMeal(local)) line = Pick(food.Lines) + "按时吃饭真棒～";
        else line = Pick(food.Lines);
        if (food.Taste == 2 && !bored) line += "（最喜欢了！）";
        AddAffection(affection);
        SetMood(bored ? "平静" : "满足", TimeSpan.FromMinutes(30));
        SaveBondSoon();
        Say(line, food.Clip);
        PetEvent("feed", food.Name, said: line, ownReaction: true);
    }

    // Which meal it is now (yyyy-MM-dd-0/1/2), or null outside meal times.
    private string? MealKey(DateTime local)
    {
        int[] hours = [preferences.BreakfastHour, preferences.LunchHour, preferences.DinnerHour];
        for (int i = 0; i < hours.Length; i++)
        {
            double minutes = Math.Abs(local.TimeOfDay.TotalMinutes - hours[i] * 60);
            if (Math.Min(minutes, 1440 - minutes) <= 45) return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + i;
        }
        return null;
    }

    private void ShowFeedPanel()
    {
        var local = DateTime.Now;
        bond.RollDay(local);
        double full = Fullness();
        var body = new StackPanel();
        var muted = Brushes.SlateGray;
        body.Children.Add(new TextBlock { Text = $"饱腹 {full:0}/100（{FullnessWord(full)}）· 今天喂了 {bond.Feeds} 次", Foreground = muted, FontSize = 12 });
        body.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = full, Height = 6, Margin = new Thickness(0, 4, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(240, 160, 90)) });
        string season = CompanionAnimationPolicy.Season(local);
        foreach (var group in Foods.GroupBy(f => KindLabel(f.Kind)))
        {
            body.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            var wrap = new WrapPanel();
            foreach (var food in group)
            {
                bool festival = food.Festival != null && FestivalNow(food.Festival, local);
                string label = food.Name + (food.Taste == 2 ? " ♥" : "") + (festival ? "（今天！）" : "");
                var item = food;
                var button = BigBlueFish.Deployment.ModernUI.Button(label, () => { petPanel?.Close(); Feed(item); }, festival);
                button.Margin = new Thickness(0, 0, 6, 6);
                button.Padding = new Thickness(10, 3, 10, 3);
                button.MinWidth = 0;
                button.ToolTip = $"拖到鱼鱼身上也能喂她{(food.Name is "冰淇淋" or "西瓜" && season == "Winter" ? "（冬天有点冰哦）" : "")}";
                MakeDraggableFood(button, food);
                wrap.Children.Add(button);
            }
            body.Children.Add(wrap);
        }
        body.Children.Add(new TextBlock { Text = "♥ 是她最爱吃的；可以点，也可以把食物拖到鱼鱼身上喂她。", Foreground = muted, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        ShowPetPanel("给鱼鱼喂点什么呢？", body, 360, true);
    }

    // ---------------------------------------------------------------- drag a food onto her
    private const string FoodDragFormat = "BigBlueFish.Food";
    private bool foodDropReady;

    private void MakeDraggableFood(Button button, Food food)
    {
        Point? start = null;
        button.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(button);
        button.PreviewMouseLeftButtonUp += (_, _) => start = null;
        button.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(button);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            EnableFoodDrop();
            DragDrop.DoDragDrop(button, new DataObject(FoodDragFormat, food.Name), DragDropEffects.Copy);
        };
    }

    private void EnableFoodDrop()
    {
        if (foodDropReady) return;
        foodDropReady = true;
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(FoodDragFormat)) return;
            e.Effects = DragDropEffects.Copy;
            if (companionReady && !PuppetActive) Pop(); // she perks up
        };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(FoodDragFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(FoodDragFormat) is not string name || Foods.FirstOrDefault(f => f.Name == name) is not { } food) return;
            e.Handled = true;
            Dispatcher.BeginInvoke(() => { petPanel?.Close(); Feed(food); });
        };
    }

    // ---------------------------------------------------------------- meal times, sneaking (CareTick)
    private DateTime nextMealNudge = DateTime.MinValue, nextSneakCheck = DateTime.MinValue, lastSneakUtc = DateTime.MinValue;

    private void FeedingTick(DateTime now, DateTime local, double idle)
    {
        double full = Fullness();
        if (!CanChatter || GameActive || FocusActive || PuppetActive || edgeHide != null || bubble.Visibility == Visibility.Visible) return;
        // At a meal time while hungry: once per meal (and once more after 等会儿).
        string? meal = MealKey(local);
        if (preferences.MealRequests && meal != null && meal != bond.MealAskKey && full < 50 && idle < 60 && now >= nextMealNudge)
        {
            bond.MealAskKey = meal;
            SaveBondSoon();
            string[] names = ["早饭", "午饭", "晚饭"];
            string which = names[meal[^1] - '0'];
            SayWithActions($"{bond.Address}，{which}时间到啦……鱼鱼的肚子在咕咕叫（饱腹 {full:0}）～要不要一起吃点东西？", "是啊，吃什么",
                ("喂她…", ShowFeedPanel),
                ("等会儿", () => { nextMealNudge = DateTime.UtcNow.AddMinutes(30); bond.MealAskKey = null; Say("好～那鱼鱼再等等，主人也别忘了吃饭哦！"); }));
            return;
        }
        // Hungry for a long while and nobody around: she quietly helps herself to a snack.
        if (!preferences.Sneaking || now < nextSneakCheck) return;
        nextSneakCheck = now.AddMinutes(1);
        if (full >= 30 || (now - bond.LastFedUtc).TotalHours < 3 || (now - lastSneakUtc).TotalHours < 2
            || (now - bond.LastInteractionUtc).TotalMinutes < 20 || animationPhase is not ("Idle" or "Event")
            || companionRandom.NextDouble() > .2 || ClipOr("大口吃零食") is not { } snack) return;
        lastSneakUtc = now;
        bond.Fullness = Math.Min(100, full + 8);
        bond.FullnessUtc = now;
        SaveBondSoon();
        CancelMovement();
        PlayAnimation(snack, "Sneak");
    }

    // ClickAnimation hook: clicked while sneaking a snack — caught!
    private bool SneakCaught()
    {
        if (animationPhase != "Sneak") return false;
        CancelMovement();
        AddAffection(.3);
        Say(Pick(new[] { "呜……被、被发现了！鱼鱼只吃了一点点……真的！", "啊！主人怎么突然出现……零食、零食是自己跑进嘴里的！", "嘘——不要告诉别人鱼鱼偷吃零食哦～" }),
            "偷吃零食被抓住");
        PetEvent("feed", "偷吃被抓住");
        return true;
    }

    // ---------------------------------------------------------------- buttons in her bubble
    private WrapPanel? bubbleActions;
    private string? bubbleActionsText;

    // Size of the button row for the bubble layout (zero when there is none).
    internal Size CompanionBubbleActionsSize(double maxWidth)
    {
        if (bubbleActions == null || bubbleActions.Visibility != Visibility.Visible) return new Size(0, 0);
        bubbleActions.Measure(new Size(maxWidth, double.PositiveInfinity));
        return bubbleActions.DesiredSize;
    }

    private void EnsureBubbleActions()
    {
        if (bubbleActions != null || bubble.Child is not Grid grid) return;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bubbleActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetRow(bubbleActions, 1);
        grid.Children.Add(bubbleActions);
        void Check(object? s, EventArgs e)
        {
            if (bubbleActionsText != null && (speech.FullText != bubbleActionsText || bubble.Visibility != Visibility.Visible)) ClearBubbleActions();
        }
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(bubbleText, Check);
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(Border)).AddValueChanged(bubble, Check);
    }

    private void ClearBubbleActions()
    {
        bubbleActionsText = null;
        if (bubbleActions == null) return;
        bubbleActions.Children.Clear();
        bubbleActions.Visibility = Visibility.Collapsed;
        CompanionEdgeLayout.Refresh(this);
    }

    // Says a line with buttons under it; a button runs its action and closes the bubble.
    private void SayWithActions(string text, string? animation, params (string Label, Action Run)[] actions)
    {
        EnsureBubbleActions();
        Say(text, animation, true);
        if (bubbleActions == null || speech.FullText != text) return;
        bubbleActions.Children.Clear();
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        bool first = true;
        foreach (var (label, run) in actions)
        {
            var button = new Button
            {
                Content = label, Margin = new Thickness(0, 0, 8, 2), Padding = new Thickness(12, 3, 12, 3), Cursor = Cursors.Hand,
                Focusable = false, FontSize = 13, BorderThickness = new Thickness(1), BorderBrush = blue,
                Background = first ? blue : Brushes.White, Foreground = first ? Brushes.White : blue
            };
            first = false;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                ClearBubbleActions();
                speech.Cancel();
                bubble.Visibility = Visibility.Collapsed;
                run();
            };
            bubbleActions.Children.Add(button);
        }
        bubbleActionsText = text;
        bubbleActions.Visibility = Visibility.Visible;
        CompanionEdgeLayout.Refresh(this);
    }
}
