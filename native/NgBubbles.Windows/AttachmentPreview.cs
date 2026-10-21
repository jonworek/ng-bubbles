using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage;

namespace NgBubbles.Windows;

public sealed class AttachmentPreview : Grid
{
    public const double PreviewWidth = 300;
    public const double ImageHeight = 220;
    private readonly Attachment attachment;
    private readonly Grid frame;
    public Task Ready { get; }
    public bool Available { get; private set; }
    public string LocalPath { get; private set; } = "";
    public static bool IsImage(Attachment value) => value.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".heic", ".heif", ".tif", ".tiff", ".webp" }.Contains(Path.GetExtension(value.TransferName).ToLowerInvariant());
    public AttachmentPreview(Attachment value, double width = PreviewWidth, bool square = false, Func<Attachment, Task>? download = null)
    {
        attachment = value;
        Width = width; HorizontalAlignment = HorizontalAlignment.Left;
        var media = IsImage(value) || value.MimeType.StartsWith("video/") || value.MimeType.StartsWith("audio/");
        Height = media ? value.MimeType.StartsWith("audio/") ? 80 : square ? width : ImageHeight : 64;
        frame = new Grid { Name = "AttachmentPreviewFrame" };
        Children.Add(new Border { Child = frame, CornerRadius = new(8), BorderThickness = new(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] });
        var name = Path.GetFileName(value.TransferName);
        var save = new Button { Content = new SymbolIcon(Symbol.Download), Width = 34, Height = 34, Padding = new(5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(6) };
        AutomationProperties.SetName(save, "Download " + name);
        ToolTipService.SetToolTip(save, "Download");
        save.Click += async (_, _) => await App.Main.RunAsync(() => download != null ? download(attachment) : App.Main.SaveAttachmentAsync(attachment));
        Children.Add(save);
        if (media)
        {
            var loading = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });

            frame.Children.Add(loading);
            Ready = LoadAsync();
        }
        else
        {
            ShowStatus(name);
            Ready = Task.CompletedTask;
        }
    }
    private void ShowStatus(string text)
    {
        frame.Children.Clear();
        frame.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 4, Margin = new(12), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = .7 });
    }
    private async Task LoadAsync()
    {
        try
        {
            LocalPath = await App.Main.Media.LoadAsync(App.Main.Client.Connection, attachment);
            if (IsImage(attachment))
            {
                var path = await App.Main.Data.NormalizeImageAsync(await File.ReadAllBytesAsync(LocalPath), 600);
                if (path.Length == 0) throw new IOException("Preview unavailable. Use Save attachment.");
                var bitmap = new BitmapImage();
                using (var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync()) await bitmap.SetSourceAsync(stream);
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                var open = new Button { Content = image, Padding = new(0), BorderThickness = new(0), Background = null,
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                AutomationProperties.SetName(open, "Open " + Path.GetFileName(attachment.TransferName) + " enlarged");
                ToolTipService.SetToolTip(open, "Click to enlarge");
                open.Click += (_, _) => App.Main.ShowAttachment(attachment, LocalPath);
                frame.Children.Clear(); frame.Children.Add(open);
            }
            else
            {
                var player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                player.Source = global::Windows.Media.Core.MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(LocalPath));
                Unloaded += (_, _) => { player.MediaPlayer?.Pause(); };
                frame.Children.Clear(); frame.Children.Add(player);
                if (attachment.MimeType.StartsWith("video/"))
                {
                    var open = new Button { Content = "Enlarge", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(6) };
                    open.Click += (_, _) => { player.MediaPlayer?.Pause(); App.Main.ShowAttachment(attachment, LocalPath); };
                    frame.Children.Add(open);
                }
            }
            Available = true;
        }
        catch (Exception error)
        {
            App.Main.Data.Log.Log("attachment-preview-failed", new { type = error.GetType().Name });
            ShowStatus("Preview unavailable.");
        }
    }
}
