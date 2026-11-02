using System.Text;
using System.Text.Json;

namespace NgBubbles.Core;

public sealed record OutgoingImage(string Id, string Name, string MimeType, long Size);
public sealed record ImageSendProgress(int Completed, int Total);

public static class ImageFiles
{
    public const long MaxBytes = 25 * 1024 * 1024;
    public const int MaxImages = 10;
    public static OutgoingImage Inspect(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length == 0 || file.Length > MaxBytes) throw new IOException("Choose images up to 25 MB each.");
        Span<byte> bytes = stackalloc byte[64]; var count = file.Read(bytes); bytes = bytes[..count];
        var brands = Encoding.ASCII.GetString(bytes);
        var type = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png" :
            bytes.StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg" :
            bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? "image/gif" :
            bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8) ? "image/webp" :
            bytes.StartsWith("BM"u8) ? "image/bmp" :
            bytes.StartsWith(new byte[] { 73, 73, 42, 0 }) || bytes.StartsWith(new byte[] { 77, 77, 0, 42 }) ? "image/tiff" :
            bytes.Length >= 12 && bytes[4..].StartsWith("ftyp"u8) && new[] { "heic", "heix", "hevc", "hevx", "mif1", "msf1" }.Any(brand => brands.Contains(brand, StringComparison.Ordinal)) ? "image/heic" : "";
        if (type.Length == 0) throw new IOException("Choose a JPEG, PNG, GIF, WebP, BMP, TIFF, or HEIC image.");
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.Contains('\r') || name.Contains('\n')) throw new IOException("Choose an image with a valid file name.");
        return new(Guid.NewGuid().ToString("N"), name, type, file.Length);
    }
}

public sealed partial class MessagingClient
{
    private readonly SemaphoreSlim imageGate = new(1, 1);
    public List<OutgoingImage> Images(string guid) { lock (gate) return Cache.ImageDrafts.GetValueOrDefault(Conversations.Id(Cache, guid))?.ToList() ?? []; }
    public string ImagePath(OutgoingImage image)
    {
        if (!Guid.TryParseExact(image.Id, "N", out _)) throw new IOException("The saved image draft is invalid. Remove it and choose the image again.");
        return Path.Combine(store.DirectoryPath, "outgoing-images", image.Id);
    }
    public async Task QueueImagesAsync(string guid, IEnumerable<string> paths)
    {
        await imageGate.WaitAsync();
        var staged = new List<OutgoingImage>();
        try
        {
            lock (gate) guid = Conversations.Id(Cache, guid);
            var files = paths.ToList();
            if (files.Count + Images(guid).Count > ImageFiles.MaxImages) throw new IOException("Attach up to 10 images per draft.");
            var inspected = files.Select(path => (Path: path, Image: ImageFiles.Inspect(path))).ToList();
            Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "outgoing-images"));
            foreach (var item in inspected)
            {
                staged.Add(item.Image);
                await using var source = File.OpenRead(item.Path);
                await using var target = File.Create(ImagePath(item.Image));
                var buffer = new byte[81920]; long copied = 0; int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    copied += read;
                    if (copied > ImageFiles.MaxBytes) throw new IOException("Choose images up to 25 MB each.");
                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
                if (copied != item.Image.Size) throw new IOException("The image changed while it was being attached. Choose it again.");
            }
            lock (gate)
            {
                if (!Cache.ImageDrafts.TryGetValue(guid, out var draft)) Cache.ImageDrafts[guid] = draft = [];
                draft.AddRange(staged);
            }
            await SaveAsync(); Publish();
        }
        catch
        {
            foreach (var image in staged) { try { File.Delete(ImagePath(image)); } catch (IOException) { } }
            lock (gate) if (Cache.ImageDrafts.TryGetValue(guid, out var draft)) draft.RemoveAll(image => staged.Contains(image));
            throw;
        }
        finally { imageGate.Release(); }
    }
    public async Task RemoveImageAsync(string guid, OutgoingImage image)
    {
        await imageGate.WaitAsync();
        try
        {
            lock (gate)
            {
                guid = Conversations.Id(Cache, guid);
                if (!Cache.ImageDrafts.TryGetValue(guid, out var images) || images.RemoveAll(candidate => candidate.Id == image.Id) == 0) return;
                if (images.Count == 0) Cache.ImageDrafts.Remove(guid);
            }
            await SaveAsync();
            try { File.Delete(ImagePath(image)); } catch (IOException) { log.Log("outgoing-image-cleanup-failed"); }
            Publish();
        }
        finally { imageGate.Release(); }
    }
    public async Task MoveImagesAsync(string from, string to)
    {
        await imageGate.WaitAsync();
        try
        {
            lock (gate)
            {
                to = Conversations.Id(Cache, to);
                if (Cache.ImageDrafts.Remove(from, out var images))
                {
                    if (!Cache.ImageDrafts.TryGetValue(to, out var target)) Cache.ImageDrafts[to] = target = [];
                    target.AddRange(images);
                }
            }
            await SaveAsync(); Publish();
        }
        finally { imageGate.Release(); }
    }
    public async Task SendImagesAsync(string guid, IProgress<ImageSendProgress>? progress = null, IReadOnlyCollection<string>? imageIds = null)
    {
        var connection = api ?? throw new IOException("Connect to your Mac before sending images.");
        var pending = Images(guid).Where(image => imageIds == null || imageIds.Contains(image.Id)).ToList(); progress?.Report(new(0, pending.Count));
        var completed = 0;
        foreach (var image in pending)
        {
            string rawGuid;
            lock (gate) rawGuid = Conversations.SendGuid(Cache, guid);
            var result = await connection.SendImageAsync(rawGuid, image, ImagePath(image), preferences.SendMethod, lifetime.Token);
            if (result.ValueKind != System.Text.Json.JsonValueKind.Object || !result.TryGetProperty("guid", out _)) throw new IOException("Image send was not confirmed. Check the conversation before retrying.");
            var message = result.Deserialize<Message>(Json.Options)!;
            Accept(message with { IsFromMe = true, ChatGuid = message.ChatGuid ?? rawGuid }, false);
            if (message.Error is not (null or 0)) throw new IOException("The server could not send this image. Your remaining images have been kept.");
            await RemoveImageAsync(guid, image);
            progress?.Report(new(++completed, pending.Count)); RequestSync();
        }
    }
}
