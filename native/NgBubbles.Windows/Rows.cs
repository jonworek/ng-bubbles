using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage;
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
    private int generation;
    public Avatar() { Width = 44; Height = 44; }
    public void Set(Chat chat, Snapshot state)
    {
        Children.Clear();
        var path = chat.IsGroup ? state.GroupPhotos.GetValueOrDefault(chat.Guid) ?? "" : Model.Photo(chat.Participants.FirstOrDefault()?.Address ?? "", state.ContactPhotos);
        var version = ++generation;
        if (!chat.IsGroup || path.Length > 0) { AddPortrait(Model.Title(chat, state.Contacts), path, 44, 0, 0, version); return; }
        var members = chat.Participants.Take(4).ToList();
        for (var i = 0; i < members.Count; i++)
        {
            var x = i % 2 * 19; var y = i / 2 * 19;
            if (members.Count == 2) { x = i * 19; y = i * 19; }
            else if (members.Count == 3) { x = i == 0 ? 9 : (i - 1) * 19; y = i == 0 ? 0 : 19; }
            AddPortrait(Model.Name(members[i].Address, state.Contacts), Model.Photo(members[i].Address, state.ContactPhotos), 25, x, y, version);
        }
        if (members.Count == 0) AddPortrait("Group", "", 44, 0, 0, version);
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
        catch { App.Main.Data.Log.Log("avatar-display-failed"); }
    }
}

public sealed class ChatRow : UserControl
{
    private ChatItem? item;
    public ChatRow()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
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
        if (item == null) return;
        var root = new Grid { Padding = new(4, 10, 4, 10), ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new() { Width = new(44) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var avatar = new Avatar(); avatar.Set(item.Chat, item.State); root.Children.Add(avatar);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var unread = item.State.Unread.Contains(item.Guid);
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = unread ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = item.Chat.LastMessage?.Preview ?? "No messages", Opacity = unread ? 1 : .65, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 12 });
        Grid.SetColumn(text, 1); root.Children.Add(text);
        var meta = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (item.Chat.LastMessage is { } last) meta.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(last.DateCreated).LocalDateTime.ToString("MMM d"), FontSize = 11, Opacity = .6 });
        if (unread) meta.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new(4), Background = new SolidColorBrush(Colors.DodgerBlue), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(meta, 2); root.Children.Add(meta); Content = root;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, item.Title + (unread ? ", unread" : ", read"));
    }
}

public sealed class MessageRow : UserControl
{
    private int generation;
    public MessageRow() { HorizontalContentAlignment = HorizontalAlignment.Stretch; DataContextChanged += (_, _) => Render(); }
    private void Render()
    {
        var version = ++generation;
        if (DataContext is not MessageItem item) { Content = null; return; }
        var message = item.Message;
        var outer = new StackPanel { Spacing = 5, Margin = new(12, 5, 12, 5), HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 720 };
        if (!message.IsFromMe && message.Handle is { } handle) outer.Children.Add(new TextBlock { Text = Model.Name(handle.Address, item.State.Contacts), FontSize = 11, Opacity = .65 });
        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(message.Text?.Replace("\uFFFC", ""))) body.Children.Add(new TextBlock { Text = message.Text.Replace("\uFFFC", ""), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15, MaxWidth = 660,
            Foreground = message.IsFromMe ? new SolidColorBrush(Colors.White) : null });
        foreach (var attachment in message.Attachments)
        {
            var host = new StackPanel { Spacing = 5, MaxWidth = 520 };
            var save = new Button { Content = "Save " + Path.GetFileName(attachment.TransferName), HorizontalAlignment = HorizontalAlignment.Left };
            save.Click += async (_, _) => await App.Main.RunAsync(() => App.Main.SaveAttachmentAsync(attachment));
            host.Children.Add(save); body.Children.Add(host);
            if (attachment.MimeType.StartsWith("image/") || attachment.MimeType.StartsWith("video/") || attachment.MimeType.StartsWith("audio/") || IsImageName(attachment.TransferName))
                _ = LoadAsync(host, attachment, version);
        }
        if (body.Children.Count == 0) body.Children.Add(new TextBlock { Text = message.Preview, IsTextSelectionEnabled = true });
        outer.Children.Add(new Border { Child = body, Padding = new(14, 10, 14, 10), CornerRadius = new(14), Background = message.IsFromMe ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 103, 192)) : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] });
        var receipt = message.IsFromMe ? message.DateRead > 0 ? " · Read" : message.DateDelivered > 0 ? " · Delivered" : "" : "";
        outer.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(message.DateCreated).LocalDateTime.ToString("MMM d, h:mm tt") + receipt, Opacity = .6, FontSize = 11, HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left });
        Content = outer;
    }
    private static bool IsImageName(string name) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".heic", ".heif", ".tif", ".tiff", ".webp" }.Contains(Path.GetExtension(name).ToLowerInvariant());
    private async Task LoadAsync(StackPanel host, Attachment attachment, int version)
    {
        try
        {
            var path = await App.Main.Media.LoadAsync(App.Main.Client.Connection, attachment);
            if (version != generation) return;
            if (attachment.MimeType.StartsWith("image/") || IsImageName(attachment.TransferName))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var png = await App.Main.Data.NormalizeImageAsync(bytes, 1280);
                if (version != generation) return;
                if (png.Length == 0) throw new IOException("Windows could not decode this image. HEIC may need the Windows HEIF codec.");
                host.Children.Insert(0, new Image { Source = new BitmapImage(new Uri(png)), MaxWidth = 520, MaxHeight = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            }
            else
            {
                var media = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false, MaxWidth = 520, Height = attachment.MimeType.StartsWith("audio/") ? 80 : 280 };
                var file = await StorageFile.GetFileFromPathAsync(path);
                media.Source = global::Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                if (version == generation) host.Children.Insert(0, media);
            }
        }
        catch (Exception e)
        {
            App.Main.Data.Log.Log("attachment-preview-failed", new { type = e.GetType().Name });
            if (version == generation) host.Children.Add(new TextBlock { Text = e is IOException ? e.Message : "Preview unavailable. Use Save attachment.", TextWrapping = TextWrapping.Wrap, Opacity = .7, MaxWidth = 400 });
        }
    }
}
