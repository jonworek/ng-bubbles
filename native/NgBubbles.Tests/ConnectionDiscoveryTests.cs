using System.Collections.Concurrent;
using System.Net;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ConnectionDiscoveryTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
    private static HttpResponseMessage Reply(object? data = null, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(Json.Write(new { status = (int)status, data })) };
    private static HttpResponseMessage Discovery(string url) => new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(new { fields = new { serverUrl = new { stringValue = url } } })) };
    [Fact]
    public async Task LocalDiscoveryVerifiesIdentityAndRotatesTheSavedFallbackWithoutSendingPasswordToGoogle()
    {
        var next = "https://new-tunnel.example.test"; var calls = new List<Uri>(); var log = new Diagnostics();
        using var api = new Api("https://expired.example.test", "private-server-secret", log, new Handler(request =>
        {
            var uri = request.RequestUri!; calls.Add(uri);
            if (uri.Host == "firestore.googleapis.com")
            { Assert.Empty(uri.Query); Assert.Null(request.Headers.Authorization); return Discovery(next); }
            Assert.Contains("password=private-server-secret", uri.Query);
            if (uri.AbsolutePath.EndsWith("/fcm/client")) return Reply(new { project_info = new { project_id = "synthetic-project" } });
            return Reply(new { computer_id = "my-mac", private_api = true });
        }), "http://mac.example.test:1234");
        var profile = await api.RefreshConnectionAsync(true);
        Assert.True(profile.LocalAvailable); Assert.True(profile.RemoteManaged);
        Assert.Equal("http://mac.example.test:1234", api.Server); Assert.Equal(next, profile.RemoteServer);
        Assert.Equal("https://expired.example.test", profile.PreviousRemote); Assert.Equal("my-mac", profile.ComputerId);
        next = "https://rotated-tunnel.example.test";
        profile = await api.RefreshConnectionAsync(true);
        Assert.Equal(next, profile.RemoteServer); Assert.Equal("https://new-tunnel.example.test", profile.PreviousRemote);
        Assert.DoesNotContain("private-server-secret", log.Report()); Assert.DoesNotContain("example.test", log.Report());
        Assert.DoesNotContain("synthetic-project", log.Report()); Assert.DoesNotContain("my-mac", log.Report());
    }
    [Fact]
    public async Task TemporaryLocalFailureFallsBackThenAutomaticallyReturnsToLocal()
    {
        var localUp = true;
        using var api = new Api("https://remote.example.test", "password", new(), new Handler(request =>
        {
            if (request.RequestUri!.Host == "mac.example.test" && !localUp) throw new HttpRequestException("Local unavailable");
            if (request.RequestUri.AbsolutePath.EndsWith("/fcm/client")) return Reply(status: HttpStatusCode.NotFound);
            return Reply(new { computer_id = "my-mac" });
        }), "http://mac.example.test:1234");
        Assert.True((await api.RefreshConnectionAsync(true)).LocalAvailable);
        localUp = false;
        Assert.False((await api.RefreshConnectionAsync(true)).LocalAvailable);
        await api.RequestAsync("/ping"); Assert.Equal("https://remote.example.test", api.Server);
        localUp = true;
        Assert.True((await api.RefreshConnectionAsync(true)).LocalAvailable);
        Assert.Equal("http://mac.example.test:1234", api.Server);
    }
    [Fact]
    public async Task UnverifiableDiscoveryKeepsTheSavedAddressAndKnownMacIdentityCannotChange()
    {
        using var api = new Api("https://saved.example.test", "password", new(), new Handler(request =>
        {
            if (request.RequestUri!.Host == "firestore.googleapis.com") return Discovery("https://wrong.example.test");
            if (request.RequestUri.AbsolutePath.EndsWith("/fcm/client")) return Reply(new { project_info = new { project_id = "synthetic-project" } });
            return Reply(new { computer_id = request.RequestUri.Host == "wrong.example.test" ? "different-mac" : "my-mac" });
        }), "http://mac.example.test:1234", "my-mac");
        var profile = await api.RefreshConnectionAsync(true);
        Assert.True(profile.LocalAvailable); Assert.False(profile.RemoteManaged); Assert.Equal("https://saved.example.test", profile.RemoteServer);
        using var wrong = new Api("https://wrong.example.test", "password", new(), new Handler(_ => Reply(new { computer_id = "different-mac" })), computerId: "my-mac");
        await Assert.ThrowsAsync<IOException>(() => wrong.ValidateConnectionAsync());
    }
    [Fact]
    public async Task LocalOnlySetupWorksWithoutRemoteAddressOrDiscoveryConfiguration()
    {
        using var api = new Api("", "password", new(), new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/fcm/client") ?
            Reply(status: HttpStatusCode.NotFound) : Reply(new { computer_id = "my-mac" })), "http://mac.example.test:1234");
        var profile = await api.ValidateConnectionAsync();
        Assert.True(profile.LocalAvailable); Assert.False(profile.RemoteManaged); Assert.Equal("", profile.RemoteServer);
        Assert.Equal("http://mac.example.test:1234", api.Server);
        Assert.Throws<ArgumentException>(() => new Api("", "password", new()));
    }
    [Fact]
    public async Task LocalSyncPublishesAProfileForPersistenceWithoutChangingTheDraftOrSelection()
    {
        await using var fixture = await Fixture.StartAsync();
        var profiles = new ConcurrentQueue<ConnectionProfile>();
        fixture.Client.SaveConnectionProfileAsync = profile => { profiles.Enqueue(profile); return Task.CompletedTask; };
        await fixture.Client.ConnectAsync(new Preferences { Server = "https://expired.example.test", LocalServer = fixture.Url }, "test-password");
        await fixture.Client.OpenAsync(fixture.Guid); fixture.Client.Draft(fixture.Guid, "Draft retained through discovery");
        await fixture.Client.SyncAsync();
        Assert.Contains(profiles, profile => profile.LocalAvailable && profile.ComputerId == "synthetic-test-mac");
        Assert.Equal(fixture.Guid, fixture.Client.Active); Assert.Equal("Draft retained through discovery", fixture.Client.Draft(fixture.Guid));
        Assert.Equal("connected", fixture.Client.Snapshot().Status);
    }
}
