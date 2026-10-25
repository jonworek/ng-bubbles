using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NgBubbles.Windows;

public sealed class SettingsTabs : UserControl
{
    public TabView Tabs { get; } = new()
    {
        IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
        TabWidthMode = TabViewWidthMode.SizeToContent
    };
    public SettingsTabs(StackPanel connection, StackPanel preferences, StackPanel support, double availableHeight)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        MinWidth = 400; Height = Math.Clamp(availableHeight - 220, 180, 440);
        var shortcuts = new StackPanel { Spacing = 16 };
        shortcuts.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var (keys, action) in new[]
        {
            ("Ctrl+Enter", "Send message"), ("Enter", "New line in a message"),
            ("Ctrl+Shift+D", "Open diagnostics"), ("Ctrl+Q", "Quit NG Bubbles"),
            ("End", "Go to the latest message when the message list has focus")
        })
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = new(130) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var label = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 1); row.Children.Add(label); shortcuts.Children.Add(row);
        }
        foreach (var (title, panel) in new[] { ("Connection", connection), ("Preferences", preferences), ("Shortcuts", shortcuts), ("Support", support) })
            Tabs.TabItems.Add(new TabViewItem { Header = title, IsClosable = false,
                Content = new ScrollViewer { Content = panel, Padding = new(0, 16, 8, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch } });
        Tabs.SelectedIndex = 0;
        Content = Tabs;
    }
}
