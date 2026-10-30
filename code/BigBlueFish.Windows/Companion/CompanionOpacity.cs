using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BigBlueFish.Windows;

// 透明度: makes her body see-through (the speech bubble keeps full opacity so it stays readable).
// Optionally she turns solid again while the mouse is on her, so she is easy to click.
public partial class MainWindow
{
    private bool opacityHover;

    private double TargetOpacity => opacityHover && preferences.OpacityHoverRestore ? 1 : Math.Clamp(preferences.PetOpacity, 20, 100) / 100.0;

    private void InitializeOpacity()
    {
        ApplyOpacity(false);
        MouseEnter += (_, _) => { opacityHover = true; ApplyOpacity(true); };
        MouseLeave += (_, _) => { opacityHover = false; ApplyOpacity(true); };
    }

    private void ApplyOpacity(bool animate) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(TargetOpacity, TimeSpan.FromMilliseconds(animate ? 180 : 0)));

    // Settings row: transparency 0–80 % (opacity 100–20 %) and the hover option.
    private void AddOpacitySettings(Panel page)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var label = new TextBlock { Width = 150, VerticalAlignment = VerticalAlignment.Center };
        void Describe() => label.Text = $"透明度：{100 - preferences.PetOpacity}%" + (preferences.PetOpacity == 100 ? "（不透明）" : "");
        Describe();
        var slider = new Slider { Minimum = 0, Maximum = 80, Value = 100 - Math.Clamp(preferences.PetOpacity, 20, 100), Width = 220,
            TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(slider, "鱼鱼的透明度");
        slider.ValueChanged += (_, e) =>
        {
            preferences.PetOpacity = 100 - (int)Math.Round(e.NewValue);
            Describe();
            ApplyOpacity(true);
            SaveCompanion();
        };
        row.Children.Add(label);
        row.Children.Add(slider);
        page.Children.Add(row);
        var hover = new CheckBox { Content = "鼠标移到鱼鱼身上时恢复不透明（方便点她、拖她）", IsChecked = preferences.OpacityHoverRestore, Margin = new Thickness(0, 2, 0, 4) };
        hover.Click += (_, _) => { preferences.OpacityHoverRestore = hover.IsChecked == true; ApplyOpacity(true); SaveCompanion(); };
        page.Children.Add(hover);
        page.Children.Add(new TextBlock { Text = "只改变鱼鱼本身，对话气泡保持清晰。", FontSize = 11, Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
    }
}
