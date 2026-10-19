using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NgBubbles.Core;

public sealed record ReleaseUpdate(string Version, Uri Installer, string Sha256, long Size);

public sealed class ReleaseUpdater : IDisposable
{
    private const string Releases = "https://github.com/jonworek/ng-bubbles/releases/download/";
    private const long MaxSize = 256L * 1024 * 1024;
    private readonly HttpClient http;
    public ReleaseUpdater(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NG-Bubbles/" + AppInfo.Version);
    }
    public async Task<ReleaseUpdate?> CheckAsync(string currentVersion, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/jonworek/ng-bubbles/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Release information exceeds the size limit.");
        using var document = JsonDocument.Parse(bytes);
        return Parse(document.RootElement, currentVersion);
    }
    public static ReleaseUpdate? Parse(JsonElement release, string currentVersion)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!System.Version.TryParse(tag.TrimStart('v'), out var version) || version.Build < 0 || version.Revision >= 0 || tag != "v" + version ||
            !System.Version.TryParse(currentVersion, out var current) || version <= current) return null;
        var name = $"NG-Bubbles-{version}-x64-Setup.exe";
        var expected = new Uri(Releases + Uri.EscapeDataString(tag) + "/" + name);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded") continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            if (url != expected.AbsoluteUri || size <= 0 || size > MaxSize || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new IOException("The release installer could not be verified.");
            return new(version.ToString(), expected, digest[7..], size);
        }
        throw new IOException("The Windows installer is not available for this release yet.");
    }
    public async Task<string> DownloadAsync(ReleaseUpdate release, string directory, IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (!System.Version.TryParse(release.Version, out var version) || release.Installer.AbsoluteUri != Releases + "v" + version + $"/NG-Bubbles-{version}-x64-Setup.exe" ||
            release.Size <= 0 || release.Size > MaxSize || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)) throw new IOException("Invalid update information.");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"NG-Bubbles-{version}-x64-Setup.exe");
        var temporary = target + "." + Guid.NewGuid() + ".partial";
        try
        {
            using var response = await http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new IOException("The installer size does not match the release.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new IOException("The installer exceeds its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report((int)(100 * total / release.Size));
                }
                await output.FlushAsync(token);
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installer download failed verification. Please try again.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => http.Dispose();
}
