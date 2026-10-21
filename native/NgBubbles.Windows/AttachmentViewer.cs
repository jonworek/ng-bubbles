using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage;
using global::Windows.System;

namespace NgBubbles.Windows;

public sealed class AttachmentViewer : Window
{
    private readonly Grid root = new() { Padding = new(16) };
    private readonly CancellationTokenSource lifetime = new();
    private MediaPlayerElement? player;
    public Task Ready { get; }
    public bool Available { get; private set; }
    public AttachmentViewer(Attachment attachment, string path)
    {
        Title = Path.GetFileName(attachment.TransferName) + " — NG Bubbles";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1000, area.Width - 40); var height = Math.Min(800, area.Height - 40);
        AppWindow.MoveAndResize(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        var close = new KeyboardAccelerator { Key = VirtualKey.Escape };
        close.Invoked += (_, args) => { args.Handled = true; Close(); }; root.KeyboardAccelerators.Add(close);
        root.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        Content = root;
        Closed += (_, _) => { lifetime.Cancel(); player?.MediaPlayer?.Dispose(); };
        Ready = LoadAsync(attachment, path);
    }
    private async Task LoadAsync(Attachment attachment, string path)
    {
        try
        {
            if (AttachmentPreview.IsImage(attachment))
            {
                var png = await App.Main.Data.NormalizeImageAsync(await File.ReadAllBytesAsync(path, lifetime.Token), 2560);
                if (lifetime.IsCancellationRequested) return;
                if (png.Length == 0) throw new IOException("Image unavailable. Use Save attachment in the conversation.");
                var bitmap = new BitmapImage();
                using (var stream = await (await StorageFile.GetFileFromPathAsync(png)).OpenReadAsync()) await bitmap.SetSourceAsync(stream);
                if (lifetime.IsCancellationRequested) return;
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                var viewer = new ScrollViewer { Content = image, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 5,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                viewer.SizeChanged += (_, _) => { image.Width = viewer.ActualWidth; image.Height = viewer.ActualHeight; };
                root.Children.Clear(); root.Children.Add(viewer);
                ToolTipService.SetToolTip(viewer, "Pinch or Ctrl+mouse wheel to zoom. Press Escape to close.");
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (lifetime.IsCancellationRequested) return;
                player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false,
                    Source = global::Windows.Media.Core.MediaSource.CreateFromStorageFile(file) };
                root.Children.Clear(); root.Children.Add(player);
            }
            Available = true;
        }
        catch (Exception error)
        {
            App.Main.Data.Log.Log("attachment-viewer-failed", new { type = error.GetType().Name });
            if (lifetime.IsCancellationRequested) return;
            root.Children.Clear(); root.Children.Add(new TextBlock { Text = "Unable to open attachment. Use Save attachment in the conversation.", TextWrapping = TextWrapping.Wrap });
        }
    }
}
