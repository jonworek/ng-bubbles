using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NgBubbles.Core;

namespace NgBubbles.Windows;

public sealed class HomeDashboard : UserControl
{
    private readonly TextBlock connection = new() { Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock conversationCount = new();
    private readonly TextBlock unreadCount = new();
    private readonly TextBlock draftCount = new();
    private readonly Dictionary<string, StackPanel> sections = [];
    private readonly Dictionary<string, (Button Button, Avatar Avatar, TextBlock Title, TextBlock Preview)> rows = [];
    public event Action<string>? OpenConversation;
    public event Action? NewConversation;
    public HomeSummary? Summary { get; private set; }
    public HomeDashboard()
    {
        var body = new StackPanel { Spacing = 24, Margin = new(24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(new TextBlock { Text = "Your inbox at a glance", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(connection);
        var counts = new Grid { ColumnSpacing = 12 };
        foreach (var _ in Enumerable.Range(0, 3)) counts.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var index = 0;
        foreach (var (label, count) in new[] { ("Unread chats", unreadCount), ("Conversations", conversationCount), ("Saved drafts", draftCount) })
        {
            count.FontSize = 30; count.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(count); stack.Children.Add(new TextBlock { Text = label, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            var card = new Border { Child = stack, Style = (Style)Application.Current.Resources["HomeCardStyle"] };
            Grid.SetColumn(card, index++); counts.Children.Add(card);
        }
        body.Children.Add(counts);
        var create = new Button { Content = "New message", HorizontalAlignment = HorizontalAlignment.Left };
        create.Click += (_, _) => NewConversation?.Invoke(); body.Children.Add(create);
        var lists = new Grid { ColumnSpacing = 20, RowSpacing = 24 };
        lists.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        foreach (var _ in Enumerable.Range(0, 3)) lists.RowDefinitions.Add(new() { Height = GridLength.Auto });
        foreach (var title in new[] { "Unread", "Recent conversations", "Saved drafts" })
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var items = new StackPanel { Spacing = 8 }; sections[title] = items;
            section.Children.Add(items); Grid.SetRow(section, lists.Children.Count); lists.Children.Add(section);
        }
        lists.SizeChanged += (_, args) =>
        {
            var columns = args.NewSize.Width >= 720 ? 2 : 1;
            if (lists.ColumnDefinitions.Count == columns) return;
            lists.ColumnDefinitions.Clear(); lists.RowDefinitions.Clear();
            for (var column = 0; column < columns; column++) lists.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            for (var row = 0; row < (3 + columns - 1) / columns; row++) lists.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var i = 0; i < lists.Children.Count; i++)
            {
                var section = (FrameworkElement)lists.Children[i];
                Grid.SetRow(section, i / columns); Grid.SetColumn(section, i % columns);
                Grid.SetColumnSpan(section, i == 2 ? columns : 1);
            }
        };
        body.Children.Add(lists);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Home dashboard");
    }
    public void Update(Snapshot state, IReadOnlyDictionary<string, string> drafts)
    {
        Summary = HomeSummary.Create(state, drafts);
        conversationCount.Text = Summary.ConversationCount.ToString(); unreadCount.Text = Summary.UnreadCount.ToString(); draftCount.Text = Summary.DraftCount.ToString();
        connection.Text = state.Status switch
        {
            "connected" => "Connected to your Mac. Choose a conversation to continue.",
            "connecting" => "Connecting to your Mac. Your saved conversations are available below.",
            _ => "You're offline. Showing your saved conversations and drafts."
        };
        Section("Unread", Summary.Unread.Select(c => (c, c.LastMessage?.Preview ?? "No messages yet")), "You're all caught up.", state);
        Section("Saved drafts", Summary.Drafts.Select(d => (d.Chat, d.Text)), "No saved drafts.", state);
        Section("Recent conversations", Summary.Recent.Select(c => (c, c.LastMessage?.Preview ?? "No messages yet")), "No conversations yet. Start a new message above.", state);
        var keep = Summary.Unread.Select(c => "Unread:" + c.Guid).Concat(Summary.Drafts.Select(d => "Saved drafts:" + d.Chat.Guid)).Concat(Summary.Recent.Select(c => "Recent conversations:" + c.Guid)).ToHashSet();
        foreach (var key in rows.Keys.Where(k => !keep.Contains(k)).ToList()) rows.Remove(key);
    }
    private void Section(string name, IEnumerable<(Chat Chat, string Preview)> source, string empty, Snapshot state)
    {
        var items = source.ToList(); var panel = sections[name];
        var keys = items.Select(item => name + ":" + item.Chat.Guid).ToList();
        if (!panel.Children.OfType<Button>().Select(button => (string)button.Tag).SequenceEqual(keys) || items.Count == 0 && panel.Children.Count == 0)
        {
            panel.Children.Clear();
            if (items.Count == 0) panel.Children.Add(new TextBlock { Text = empty, Opacity = .65, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) });
            foreach (var (chat, _) in items)
            {
                var key = name + ":" + chat.Guid;
                if (!rows.TryGetValue(key, out var row))
                {
                    var avatar = new Avatar();
                    var title = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                    var preview = new TextBlock { FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
                    var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(title); text.Children.Add(preview);
                    var layout = new Grid { ColumnSpacing = 12 };
                    layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                    layout.Children.Add(avatar); Grid.SetColumn(text, 1); layout.Children.Add(text);
                    var button = new Button { Content = layout, Tag = key, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(12) };
                    var guid = chat.Guid; button.Click += (_, _) => OpenConversation?.Invoke(guid);
                    rows[key] = row = (button, avatar, title, preview);
                }
                panel.Children.Add(row.Button);
            }
        }
        foreach (var (chat, preview) in items)
        {
            var row = rows[name + ":" + chat.Guid];
            row.Title.Text = Model.Title(chat, state.Contacts); row.Preview.Text = preview;
            row.Title.FontWeight = state.Unread.Contains(chat.Guid) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
            row.Avatar.Set(chat, state);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Button, row.Title.Text + (state.Unread.Contains(chat.Guid) ? ", unread" : ", read") + ", " + preview);
        }
    }
}
