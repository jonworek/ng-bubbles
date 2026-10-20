using NgBubbles.Core;
using System.Security.Cryptography;
using System.Text;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage.Streams;

namespace NgBubbles.Windows;

internal sealed class NotificationAvatar(LocalData data)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> GetAsync(Chat chat, Snapshot state)
    {
        var photo = chat.IsGroup ? state.GroupPhotos.GetValueOrDefault(chat.Guid) ?? "" :
            Model.Photo(chat.Participants.FirstOrDefault()?.Address ?? "", state.ContactPhotos);
        if (Path.IsPathRooted(photo) && File.Exists(photo)) return photo;
        var members = chat.IsGroup ? chat.Participants.Take(4).ToArray() : chat.Participants.Take(1).ToArray();
        var paths = members.Select(member => Model.Photo(member.Address, state.ContactPhotos))
            .Select(path => Path.IsPathRooted(path) && File.Exists(path) ? path : "").ToArray();
        if (paths.Length == 0) paths = [""];
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("v1|" + string.Join("|", paths))));
        var directory = Path.Combine(data.Store.DirectoryPath, "notification-avatars");
        var target = Path.Combine(directory, key + ".png");
        await gate.WaitAsync();
        try
        {
            if (File.Exists(target)) return target;
            const int edge = 192;
            var pixels = new byte[edge * edge * 4];
            for (var i = 0; i < paths.Length; i++)
            {
                var size = paths.Length == 1 ? edge : 109;
                var x = i % 2 * 83; var y = i / 2 * 83;
                if (paths.Length == 2) { x = i * 83; y = i * 83; }
                else if (paths.Length == 3) { x = i == 0 ? 41 : (i - 1) * 83; y = i == 0 ? 0 : 83; }
                byte[]? portrait = null;
                if (paths[i].Length > 0)
                {
                    try { portrait = await DecodeAsync(paths[i], (uint)size); }
                    catch (Exception e) { data.Log.Log("notification-avatar-decode-failed", new { type = e.GetType().Name }); }
                }
                DrawPortrait(pixels, edge, portrait, size, x, y);
            }
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, edge, edge, 96, 96, pixels);
            await encoder.FlushAsync();
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            var png = new byte[output.Size]; reader.ReadBytes(png);
            Directory.CreateDirectory(directory);
            var temporary = target + ".tmp";
            await File.WriteAllBytesAsync(temporary, png);
            File.Move(temporary, target, true);
            return target;
        }
        finally { gate.Release(); }
    }
    private static async Task<byte[]> DecodeAsync(string path, uint size)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var scale = Math.Max(size / (double)decoder.PixelWidth, size / (double)decoder.PixelHeight);
        var width = Math.Max(size, (uint)Math.Ceiling(decoder.PixelWidth * scale));
        var height = Math.Max(size, (uint)Math.Ceiling(decoder.PixelHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (width - size) / 2, Y = (height - size) / 2, Width = size, Height = size }
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return pixels.DetachPixelData();
    }
    private static void DrawPortrait(byte[] canvas, int edge, byte[]? portrait, int size, int left, int top)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nx = (x + .5) / size; var ny = (y + .5) / size;
            if (Math.Pow(nx - .5, 2) + Math.Pow(ny - .5, 2) > .25) continue;
            var destination = ((top + y) * edge + left + x) * 4;
            var source = (y * size + x) * 4;
            var silhouette = Math.Pow(nx - .5, 2) + Math.Pow(ny - .35, 2) < .0225 ||
                Math.Pow((nx - .5) / .32, 2) + Math.Pow((ny - .85) / .32, 2) < 1;
            byte blue = silhouette ? (byte)240 : (byte)157;
            byte green = silhouette ? (byte)230 : (byte)117;
            byte red = silhouette ? (byte)220 : (byte)82;
            if (portrait != null)
            {
                var alpha = portrait[source + 3] / 255d;
                blue = (byte)(portrait[source] * alpha + blue * (1 - alpha));
                green = (byte)(portrait[source + 1] * alpha + green * (1 - alpha));
                red = (byte)(portrait[source + 2] * alpha + red * (1 - alpha));
            }
            canvas[destination] = blue; canvas[destination + 1] = green; canvas[destination + 2] = red; canvas[destination + 3] = 255;
        }
    }
}
