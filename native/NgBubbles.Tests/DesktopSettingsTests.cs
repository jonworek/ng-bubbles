using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class DesktopSettingsTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static string Release(byte[] bytes, string tag = "v0.4.1", bool prerelease = false, string? url = null, string? digest = null) => Json.Write(new
    {
        tag_name = tag, draft = false, prerelease,
        assets = new[] { new { name = "NG-Bubbles-0.4.1-x64-Setup.exe", state = "uploaded", size = bytes.Length,
            browser_download_url = url ?? "https://github.com/jonworek/ng-bubbles/releases/download/v0.4.1/NG-Bubbles-0.4.1-x64-Setup.exe",
            digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) } }
    });
    [Fact]
    public async Task WindowSizeSurvivesNewStoreAndConnectionSettingsSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-window-" + Guid.NewGuid());
        try
        {
            var first = new Store(directory);
            var saved = new WindowSettings(1536, 944, true);
            await first.SaveAsync("window.json", saved);
            await first.SaveAsync("settings.json", new Preferences { Server = "https://example.invalid" });
            var restored = new Store(directory).Load("window.json", () => new WindowSettings());
            Assert.Equal(saved, restored);
            Assert.Equal((3072, 1888), restored.Fit(3840, 2160, 2));
            Assert.Equal((992, 736), restored.Fit(1024, 768, 2));
            Assert.Equal((2800, 1800), new WindowSettings().Fit(3840, 2160, 2));
            Assert.Equal((1400, 900), new WindowSettings(double.NaN, -1).Fit(1920, 1080, 1));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task LatestStableReleaseDownloadsOnlyAfterExplicitDownloadAndVerifiesBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("Synthetic installer bytes, never executed");
        var requests = new List<string>();
        using var updater = new ReleaseUpdater(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = requests.Count == 1 ? new StringContent(Release(bytes)) : new ByteArrayContent(bytes) };
        }));
        var directory = Path.Combine(Path.GetTempPath(), "ng-update-" + Guid.NewGuid());
        try
        {
            var release = await updater.CheckAsync("0.4.0");
            Assert.NotNull(release);
            Assert.Single(requests);
            var file = await updater.DownloadAsync(release, directory);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, requests.Count);
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void IgnoreOldOrPrereleaseVersionsAndRejectUnexpectedDownloadLocations()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Null(ReleaseUpdater.Parse(JsonDocument.Parse(Release(bytes)).RootElement, "0.4.1"));
        Assert.Null(ReleaseUpdater.Parse(JsonDocument.Parse(Release(bytes, prerelease: true)).RootElement, "0.4.0"));
        Assert.Throws<IOException>(() => ReleaseUpdater.Parse(JsonDocument.Parse(Release(bytes, url: "https://evil.invalid/installer.exe")).RootElement, "0.4.0"));
        Assert.Throws<IOException>(() => ReleaseUpdater.Parse(JsonDocument.Parse(Release(bytes, digest: "")).RootElement, "0.4.0"));
    }
    [Fact]
    public async Task BadOrTruncatedInstallerNeverBecomesExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-update-bad-" + Guid.NewGuid());
        var expected = new byte[] { 1, 2, 3 };
        var release = ReleaseUpdater.Parse(JsonDocument.Parse(Release(expected)).RootElement, "0.4.0")!;
        try
        {
            using var corrupt = new ReleaseUpdater(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) }));
            await Assert.ThrowsAsync<IOException>(() => corrupt.DownloadAsync(release, directory));
            Assert.Empty(Directory.GetFiles(directory));
            using var truncated = new ReleaseUpdater(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
            await Assert.ThrowsAsync<IOException>(() => truncated.DownloadAsync(release, directory));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
