using NgBubbles.Core;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NgBubbles.Windows;

public sealed class MediaCache(LocalData data)
{
    private readonly SemaphoreSlim transfers = new(3, 3);
    private readonly ConcurrentDictionary<string, Task<string>> pending = new();
    public Task<string> LoadAsync(Api api, Attachment attachment)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(api.Server + ":" + attachment.Guid)));
        return pending.GetOrAdd(key, _ => DownloadAsync(api, attachment, key));
    }
    private async Task<string> DownloadAsync(Api api, Attachment attachment, string key)
    {
        await transfers.WaitAsync();
        try
        {
            var root = Path.Combine(data.Store.DirectoryPath, "attachments");
            Directory.CreateDirectory(root);
            var extension = Path.GetExtension(Path.GetFileName(attachment.TransferName));
            if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
            var target = Path.Combine(root, key + extension);
            if (File.Exists(target)) { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); return target; }
            if (attachment.TotalBytes > 16 * 1024 * 1024) throw new IOException("Preview exceeds 16 MB. Use Save attachment.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var bytes = await api.BytesAsync($"/attachment/{Uri.EscapeDataString(attachment.Guid)}/download", new() { ["original"] = "false" }, token: timeout.Token) ?? throw new IOException("Attachment unavailable.");
            await File.WriteAllBytesAsync(target + ".tmp", bytes); File.Move(target + ".tmp", target, true);
            var files = new DirectoryInfo(root).GetFiles().Where(f => f.Extension != ".tmp").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files) { if (total <= 100 * 1024 * 1024) break; if (file.FullName == target) continue; try { total -= file.Length; file.Delete(); } catch (IOException) { } }
            data.Log.Log("attachment-loaded", new { bytes = bytes.Length });
            return target;
        }
        finally { transfers.Release(); pending.TryRemove(key, out _); }
    }
}
