using NgBubbles.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage.Streams;

namespace NgBubbles.Windows;

public sealed class LocalData
{
    public Store Store { get; }
    public Preferences Settings { get; private set; }
    public Diagnostics Log { get; } = new();
    private readonly SemaphoreSlim images = new(2, 2);
    public LocalData(string? overrideDirectory = null)
    {
        var root = overrideDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NG Bubbles");
        Store = new(root);
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueBubbles Desktop");
        if (overrideDirectory == null && !File.Exists(Path.Combine(root, "settings.json")) && Directory.Exists(legacy))
        {
            Directory.CreateDirectory(root);
            foreach (var name in new[] { "settings.json", "cache.json" })
                if (File.Exists(Path.Combine(legacy, name))) File.Copy(Path.Combine(legacy, name), Path.Combine(root, name), false);
        }
        Settings = Store.Load("settings.json", () => new Preferences());
        if (overrideDirectory == null && Directory.Exists(Path.Combine(legacy, "Local Storage")))
            Settings.MigrationNotice = "Connection settings and cached history were imported. Old browser drafts remain in the previous app; finish or copy them before removing it.";
    }
    public string Password()
    {
        if (Settings.Secret.Length == 0) return "";
        try
        {
            var encrypted = Convert.FromBase64String(Settings.Secret);
            // Electron safeStorage on Windows prepends v10 to a DPAPI-protected blob.
            if (encrypted.Length > 3 && encrypted[0] == 'v' && encrypted[1] == '1' && encrypted[2] == '0') encrypted = encrypted[3..];
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        { Log.Log("credential-import-needs-password"); return ""; }
    }
    public async Task SaveSettingsAsync(Preferences settings, string password)
    {
        settings.Secret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        await Store.SaveAsync("settings.json", settings);
        Settings = settings;
    }
    public async Task<string> PhotoAsync(JsonElement raw)
    {
        byte[] bytes;
        try
        {
            if (raw.ValueKind == JsonValueKind.String)
            {
                var value = raw.GetString()!;
                if (value.Length > 24 * 1024 * 1024) return "";
                if (Path.IsPathRooted(value) && File.Exists(value)) return value;
                bytes = Convert.FromBase64String(value.StartsWith("data:") ? value[(value.IndexOf(',') + 1)..] : value);
            }
            else if (raw.ValueKind == JsonValueKind.Array) bytes = raw.EnumerateArray().Select(v => v.GetByte()).ToArray();
            else if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("data", out var data)) bytes = data.EnumerateArray().Select(v => v.GetByte()).ToArray();
            else return "";
            if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) return "";
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException) { return ""; }
        return await NormalizeImageAsync(bytes, 96);
    }
    public async Task<string> NormalizeImageAsync(byte[] bytes, uint edge)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Store.DirectoryPath, "photos");
        var path = Path.Combine(root, key + "-" + edge + ".png");
        if (File.Exists(path)) return path;
        await images.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(root);
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
            var decoder = await BitmapDecoder.CreateAsync(input);
            if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 64000000) throw new IOException("Image exceeds pixel limit.");
            var scale = Math.Min(1d, edge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            var png = new byte[output.Size]; reader.ReadBytes(png);
            var temporary = path + "." + Guid.NewGuid() + ".tmp";
            await File.WriteAllBytesAsync(temporary, png); File.Move(temporary, path, true);
            return path;
        }
        catch (Exception e) { Log.Log("image-decode-failed", new { type = e.GetType().Name, bytes = bytes.Length }); return ""; }
        finally { images.Release(); }
    }
}
