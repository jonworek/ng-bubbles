using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage.Streams;

namespace NgBubbles.Windows;

public sealed class ChatItem(Chat chat, Snapshot snapshot)
{
    public Chat Chat { get; private set; } = chat;
    public Snapshot State { get; private set; } = snapshot;
    public string Guid => Chat.Guid;
    public string Title => Model.Title(Chat, State.Contacts);
    public event Action? Updated;
    public void Update(Chat value, Snapshot state) { Chat = value; State = state; Updated?.Invoke(); }
}
public sealed record MessageItem(Message Message, Snapshot State);

public sealed class Avatar : Grid
{
    private sealed record Portrait(string Name, string Path, double Size, double X, double Y);
    private List<Portrait> portraits = [];
    private int generation;
    public Avatar() { Width = 44; Height = 44; }
    public void Set(Chat chat, Snapshot state)
    {
        var path = chat.IsGroup ? state.GroupPhotos.GetValueOrDefault(chat.Guid) ?? "" : Model.Photo(chat.Participants.FirstOrDefault()?.Address ?? "", state.ContactPhotos);
        var next = new List<Portrait>();
        if (!chat.IsGroup || path.Length > 0) next.Add(new(Model.Title(chat, state.Contacts), path, 44, 0, 0));
        else
        {
            var members = chat.Participants.Take(4).ToList();
            for (var i = 0; i < members.Count; i++)
            {
                var x = i % 2 * 19; var y = i / 2 * 19;
                if (members.Count == 2) { x = i * 19; y = i * 19; }
                else if (members.Count == 3) { x = i == 0 ? 9 : (i - 1) * 19; y = i == 0 ? 0 : 19; }
                next.Add(new(Model.Name(members[i].Address, state.Contacts), Model.Photo(members[i].Address, state.ContactPhotos), 25, x, y));
            }
            if (members.Count == 0) next.Add(new("Group", "", 44, 0, 0));
        }
        if (portraits.SequenceEqual(next)) return;
        portraits = next;
        var version = ++generation;
        Children.Clear();
        foreach (var portrait in portraits) AddPortrait(portrait.Name, portrait.Path, portrait.Size, portrait.X, portrait.Y, version);
    }
    private async void AddPortrait(string name, string path, double size, double x, double y, int version)
    {
        var picture = new PersonPicture { DisplayName = name, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(x, y, 0, 0) };
        Children.Add(picture);
        if (path.Length == 0) return;
        try
        {
            var image = new BitmapImage { DecodePixelWidth = 96 };
            if (path.StartsWith("data:", StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(path[(path.IndexOf(',') + 1)..]);
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
                await image.SetSourceAsync(stream);
            }
            else image.UriSource = new Uri(path);
            if (version == generation) picture.ProfilePicture = image;
        }
        catch { App.Main?.Data.Log.Log("avatar-display-failed"); }
    }
}

public sealed class ChatRow : UserControl
{
    private ChatItem? item;
    private readonly Avatar avatar = new();
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock preview = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 12 };
    private readonly TextBlock timestamp = new() { FontSize = 11, Opacity = .6 };
    private readonly Border unreadDot = new() { Width = 8, Height = 8, CornerRadius = new(4), Background = new SolidColorBrush(Colors.DodgerBlue), HorizontalAlignment = HorizontalAlignment.Right };
    public ChatRow()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new Grid { Padding = new(4, 10, 4, 10), ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new() { Width = new(44) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        root.Children.Add(avatar);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title); text.Children.Add(preview); Grid.SetColumn(text, 1); root.Children.Add(text);
        var meta = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        meta.Children.Add(timestamp); meta.Children.Add(unreadDot); Grid.SetColumn(meta, 2); root.Children.Add(meta);
        Content = root;
        var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "Delete conversation…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => { if (item != null && App.Main is { } window) await window.ConfirmDeleteAsync(window.DeletionTargetsForRow(item.Guid)); };
        var select = new MenuFlyoutItem { Text = "Select conversations…" };
        select.Click += (_, _) => { if (item != null) App.Main?.BeginConversationSelection(item.Guid); };
        menu.Opening += (_, _) =>
        {
            var count = item == null ? 1 : App.Main?.DeletionTargetsForRow(item.Guid).Count ?? 1;
            delete.Text = count > 1 ? $"Delete {count} selected conversations…" : "Delete conversation…";
        };
        menu.Items.Add(delete); menu.Items.Add(select); ContextFlyout = menu;
        DataContextChanged += (_, _) =>
        {
            if (item != null) item.Updated -= Render;
            item = DataContext as ChatItem;
            if (item != null) item.Updated += Render;
            Render();
        };
    }
    private void Render()
    {
        Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
        if (item == null) return;
        avatar.Set(item.Chat, item.State);
        var unread = item.State.Unread.Contains(item.Guid);
        title.Text = item.Title;
        title.FontWeight = unread ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
        preview.Text = item.Chat.LastMessage?.Preview ?? "No messages";
        preview.Opacity = unread ? 1 : .65;
        timestamp.Text = item.Chat.LastMessage is { } last ? DateTimeOffset.FromUnixTimeMilliseconds(last.DateCreated).LocalDateTime.ToString("MMM d") : "";
        unreadDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, item.Title + (unread ? ", unread" : ", read"));
    }
}

public sealed class MessageRow : UserControl
{
    public MessageRow() { HorizontalContentAlignment = HorizontalAlignment.Stretch; DataContextChanged += (_, _) => Render(); }
    private void Render()
    {
        if (DataContext is not MessageItem item) { Content = null; return; }
        var message = item.Message;
        var outer = new StackPanel { Spacing = 5, Margin = new(12, 5, 12, 5), HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 720 };
        if (!message.IsFromMe && message.Handle is { } handle) outer.Children.Add(new TextBlock { Text = Model.Name(handle.Address, item.State.Contacts), FontSize = 11, Opacity = .65 });
        var body = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        var link = RichLinks.Read(message);
        if (!(link != null && RichLinks.SafeUrl(message.Text?.Replace("\uFFFC", "").Trim()) == link.Url) && !string.IsNullOrWhiteSpace(message.Text?.Replace("\uFFFC", "")))
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15, MaxWidth = 660 };
            var value = message.Text.Replace("\uFFFC", "");
            var links = MessageLinks.Find(value);
            if (links.Count == 0) text.Text = value;
            else
            {
                var position = 0;
                foreach (var web in links)
                {
                    if (web.Start > position) text.Inlines.Add(new Run { Text = value[position..web.Start] });
                    var target = new Hyperlink();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(target, web.Url);
                    ToolTipService.SetToolTip(target, web.Url);
                    target.Click += async (_, _) => await App.Main.RunAsync(async () =>
                    {
                        if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri(web.Url))) throw new IOException("Windows could not open this link.");
                    });
                    target.Inlines.Add(new Run { Text = value.Substring(web.Start, web.Length) });
                    if (message.IsFromMe) target.Foreground = new SolidColorBrush(Colors.White);
                    text.Inlines.Add(target); position = web.Start + web.Length;
                }
                if (position < value.Length) text.Inlines.Add(new Run { Text = value[position..] });
            }
            if (message.IsFromMe) text.Foreground = new SolidColorBrush(Colors.White);
            body.Children.Add(text);
        }
        if (link != null) body.Children.Add(new LinkPreview(message, link));
        var attachments = message.Attachments.Where(attachment => link == null || !RichLinks.IsPlugin(attachment)).ToList();
        if (attachments.Count > 1 && attachments.All(AttachmentPreview.IsImage))
        {
            var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            tiles.ColumnDefinitions.Add(new() { Width = new(146) }); tiles.ColumnDefinitions.Add(new() { Width = new(146) });
            for (var i = 0; i < attachments.Count; i++)
            {
                if (i % 2 == 0) tiles.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var tile = new AttachmentPreview(attachments[i], 146, square: true);
                Grid.SetColumn(tile, i % 2); Grid.SetRow(tile, i / 2); tiles.Children.Add(tile);
            }
            body.Children.Add(tiles);
        }
        else foreach (var attachment in attachments) body.Children.Add(new AttachmentPreview(attachment));
        if (body.Children.Count == 0) body.Children.Add(new TextBlock { Text = message.Preview, IsTextSelectionEnabled = true });
        var bubble = new Border { Name = "MessageBubble", Child = body, HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left, Padding = new(12, 8, 12, 8), CornerRadius = new(14), Background = message.IsFromMe ? new SolidColorBrush(Model.IsTextMessage(message) ? global::Windows.UI.Color.FromArgb(255, 30, 128, 48) : global::Windows.UI.Color.FromArgb(255, 0, 103, 192)) : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        if (message.Reactions.Count == 0) outer.Children.Add(bubble);
        else
        {
            var host = new Grid { HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            bubble.Margin = new(0, 14, 0, 0); host.Children.Add(bubble);
            var label = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Segoe UI Emoji"), MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = string.Join(" ", message.Reactions.GroupBy(reaction => reaction.Emoji).Select(group => group.Key + (group.Count() > 1 ? " " + group.Count() : ""))) };
            var badge = new Border { Name = "MessageReactions", Child = label, Padding = new(6, 2, 6, 2), CornerRadius = new(12), BorderThickness = new(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
            var description = string.Join("\n", message.Reactions.Select(reaction => (reaction.IsFromMe ? "You" : reaction.Sender.Length > 0 ? Model.Name(reaction.Sender, item.State.Contacts) : "Someone") + " reacted " + reaction.Emoji));
            ToolTipService.SetToolTip(badge, description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(badge, description);
            var overlay = new Canvas(); overlay.Children.Add(badge); host.Children.Add(overlay);
            void PositionBadge() => Canvas.SetLeft(badge, message.IsFromMe ? Math.Min(8, bubble.ActualWidth - badge.ActualWidth) : 8);
            badge.Loaded += (_, _) => PositionBadge(); bubble.SizeChanged += (_, _) => PositionBadge();
            outer.Children.Add(host);
        }
        var receipt = message.IsFromMe ? message.Error is not (null or 0) ? " · Not sent" : message.DateRead > 0 ? " · Read" : message.DateDelivered > 0 ? " · Delivered" : "" : "";
        outer.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(message.DateCreated).LocalDateTime.ToString("MMM d, h:mm tt") + receipt, Opacity = .6, FontSize = 11, HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left });
        Content = outer;
    }
}
