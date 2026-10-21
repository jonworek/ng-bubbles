using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NgBubbles.Core;
using global::Windows.Storage;

namespace NgBubbles.Windows;

public sealed class LinkPreview : Border
{
    private readonly HyperlinkButton open;
    private readonly Grid picture = new() { Height = 128 };
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock site = new() { FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis };
    public Task Ready { get; }
    public LinkPreview(Message message, RichLink link)
    {
        Width = 300; Height = 228; CornerRadius = new(8); BorderThickness = new(1);
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        var panel = new Grid { RowSpacing = 6 };
        panel.RowDefinitions.Add(new() { Height = new(128) }); panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = new(20) });
        picture.Children.Add(new SymbolIcon(Symbol.Link) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(picture); Grid.SetRow(title, 1); panel.Children.Add(title); Grid.SetRow(site, 2); panel.Children.Add(site);
        title.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]; site.Foreground = title.Foreground;
        open = new HyperlinkButton { Content = panel, Padding = new(8), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Child = open; SetLink(link);
        Ready = LoadAsync(message);
    }
    private void SetLink(RichLink link)
    {
        title.Text = link.Title; site.Text = link.Site;
        open.NavigateUri = RichLinks.SafeUrl(link.Url) is { } url ? new Uri(url) : null;
        open.IsEnabled = open.NavigateUri != null;
        AutomationProperties.SetName(open, "Open link: " + link.Title);
        ToolTipService.SetToolTip(open, open.NavigateUri?.AbsoluteUri ?? "Link preview unavailable");
    }
    private async Task LoadAsync(Message message)
    {
        try
        {
            message = await App.Main.Client.LinkDetailsAsync(message);
            var link = RichLinks.Read(message);
            if (link == null) return;
            SetLink(link);
            var candidates = message.Attachments.Where(RichLinks.IsPlugin).ToList();
            if (link.ImageIndex is { } index && index < message.Attachments.Count)
            {
                var preferred = message.Attachments[index];
                if (RichLinks.IsPlugin(preferred)) { candidates.Remove(preferred); candidates.Insert(0, preferred); }
            }
            BitmapImage? best = null; long area = 0;
            foreach (var attachment in candidates.Take(4))
            {
                try
                {
                    var path = await App.Main.Media.LoadAsync(App.Main.Client.Connection, attachment);
                    var normalized = await App.Main.Data.NormalizeImageAsync(await File.ReadAllBytesAsync(path), 600);
                    if (normalized.Length == 0) continue;
                    var bitmap = new BitmapImage();
                    using (var stream = await (await StorageFile.GetFileFromPathAsync(normalized)).OpenReadAsync()) await bitmap.SetSourceAsync(stream);
                    var size = (long)bitmap.PixelWidth * bitmap.PixelHeight;
                    if (size > area) { best = bitmap; area = size; }
                }
                catch (Exception error) { App.Main.Data.Log.Log("link-image-unavailable", new { type = error.GetType().Name }); }
            }
            if (best != null) { picture.Children.Clear(); picture.Children.Add(new Image { Source = best, Stretch = Stretch.Uniform }); }
        }
        catch (Exception error) { App.Main.Data.Log.Log("link-preview-unavailable", new { type = error.GetType().Name }); }
    }
}
