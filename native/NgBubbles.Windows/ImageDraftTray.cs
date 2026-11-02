using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage.Pickers;

namespace NgBubbles.Windows;

public sealed class ImageDraftTray : UserControl
{
    private readonly MainWindow owner;
    private readonly StackPanel previews = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Visibility = Visibility.Collapsed };
    private string? guid;
    private string signature = "";
    public Button Attach { get; } = new() { Content = new SymbolIcon(Symbol.Attach), VerticalAlignment = VerticalAlignment.Bottom, MinHeight = 48 };
    public int Count => guid == null ? 0 : owner.Client.Images(guid).Count;
    public ImageDraftTray(MainWindow owner)
    {
        this.owner = owner;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new ScrollViewer { Content = previews, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 120 });
        panel.Children.Add(status); Content = panel;
        Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(Attach, "Attach images");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Attach, "Attach images");
        Attach.Click += async (_, _) =>
        {
            try { await ChooseAsync(); }
            catch (Exception e) { owner.Data.Log.Log("image-pick-failed", new { type = e.GetType().Name }); status.Text = e is IOException ? e.Message : "The images could not be attached. Try choosing them again."; status.Visibility = Visibility.Visible; Visibility = Visibility.Visible; }
        };
    }
    public void Bind(string? target, bool enabled = true)
    {
        guid = target; Attach.IsEnabled = target != null && enabled;
        var images = target == null ? [] : owner.Client.Images(target);
        var next = target + ":" + string.Join('|', images.Select(image => image.Id));
        previews.IsEnabled = enabled;
        Visibility = images.Count == 0 && status.Visibility == Visibility.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (next == signature) return;
        signature = next; previews.Children.Clear();
        foreach (var image in images)
        {
            var tile = new StackPanel { Width = 90, Spacing = 3 };
            var thumbnail = new Image { Width = 90, Height = 72, Stretch = Stretch.Uniform };
            var fallback = new TextBlock { Text = "Image", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var preview = new Grid { Width = 90, Height = 72 }; preview.Children.Add(fallback); preview.Children.Add(thumbnail);
            thumbnail.ImageOpened += (_, _) => fallback.Visibility = Visibility.Collapsed;
            thumbnail.Source = new BitmapImage(new Uri(owner.Client.ImagePath(image))) { DecodePixelWidth = 180 };
            var show = new Button { Content = preview, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, "Preview " + image.Name);
            show.Click += (_, _) => owner.ViewOutgoingImage(image);
            var remove = new Button { Content = new SymbolIcon(Symbol.Cancel), Padding = new(4), HorizontalAlignment = HorizontalAlignment.Center };
            ToolTipService.SetToolTip(remove, "Remove image");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove " + image.Name);
            var boundGuid = target!;
            remove.Click += async (_, _) => await owner.RunAsync(async () => { await owner.Client.RemoveImageAsync(boundGuid, image); Bind(guid, Attach.IsEnabled); });
            tile.Children.Add(show); tile.Children.Add(remove); previews.Children.Add(tile);
        }
    }
    private async Task ChooseAsync()
    {
        var target = guid;
        if (target == null) return;
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        Attach.IsEnabled = false; status.Text = "Adding images…"; status.Visibility = Visibility.Visible; Visibility = Visibility.Visible;
        try { await Task.Run(() => owner.Client.QueueImagesAsync(target, files.Select(file => file.Path))); status.Visibility = Visibility.Collapsed; }
        finally { Bind(guid); }
    }
}
