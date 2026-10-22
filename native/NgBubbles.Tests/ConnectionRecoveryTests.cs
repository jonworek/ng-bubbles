using System.Net;
using System.Text.Json;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ConnectionRecoveryTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private static HttpResponseMessage Reply(object? data = null, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(Json.Write(new { status = (int)status, data })) };
    [Fact]
    public async Task LocalAddressIsPreferredAndEveryRequestRemainsAuthenticated()
    {
        var calls = new List<Uri>();
        using var api = new Api("https://tunnel.example.test", "secret value", new(), new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!);
            return Task.FromResult(Reply("pong"));
        }), "http://mac.example.test:1234");
        await api.RequestAsync("/ping");
        Assert.Equal("http://mac.example.test:1234", api.Server);
        Assert.Single(calls);
        Assert.Contains("password=secret%20value", calls[0].Query);
    }
    [Fact]
    public async Task ReadOnlyPostFallsBackToTunnelAndSticksToWorkingAddress()
    {
        var calls = new List<string>();
        var log = new Diagnostics();
        using var api = new Api("https://tunnel.example.test", "private-password", log, new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "mac.example.test") throw new HttpRequestException(HttpRequestError.NameResolutionError, "Sensitive URL must not be logged");
            return Task.FromResult(Reply(Array.Empty<Chat>()));
        }), "http://mac.example.test:1234");
        Assert.Empty(await api.PagesAsync<Chat>("/chat/query"));
        await api.RequestAsync("/ping");
        Assert.Equal(new[] { "mac.example.test", "tunnel.example.test", "tunnel.example.test" }, calls);
        Assert.Equal("https://tunnel.example.test", api.Server);
        Assert.Contains("NameResolutionError", log.Report());
        Assert.DoesNotContain("private-password", log.Report());
        Assert.DoesNotContain("example.test", log.Report());
    }
    [Fact]
    public async Task BrokenTunnelCanRecoverThroughLocalAddressAfterLocalBecomesAvailable()
    {
        var localUp = false; var tunnelUp = true;
        using var api = new Api("https://tunnel.example.test", "password", new(), new Handler((request, _) =>
        {
            var up = request.RequestUri!.Host == "mac.example.test" ? localUp : tunnelUp;
            return Task.FromResult(Reply("pong", up ? HttpStatusCode.OK : HttpStatusCode.BadGateway));
        }), "http://mac.example.test:1234");
        await api.RequestAsync("/ping");
        Assert.Equal("https://tunnel.example.test", api.Server);
        tunnelUp = false; localUp = true;
        await api.RequestAsync("/ping");
        Assert.Equal("http://mac.example.test:1234", api.Server);
    }
    [Fact]
    public async Task InvalidTunnelResponseFallsBackButWrongPasswordDoesNot()
    {
        var calls = 0; var invalid = true;
        using var api = new Api("https://tunnel.example.test", "password", new(), new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(invalid && calls == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Unavailable</html>") } : Reply("pong", invalid ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }), "http://mac.example.test:1234");
        await api.RequestAsync("/ping");
        Assert.Equal(2, calls);
        calls = 0; invalid = false;
        await Assert.ThrowsAsync<IOException>(() => api.RequestAsync("/ping"));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task SendsAndRestartsAreNeverReplayedAfterAmbiguousFailure()
    {
        var calls = new List<string>(); var failRestart = false;
        using var api = new Api("https://tunnel.example.test", "password", new(), new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post || failRestart && request.RequestUri.AbsolutePath.EndsWith("/restart/hard")) throw new HttpRequestException("Response lost after request reached the server");
            return Task.FromResult(Reply("pong"));
        }), "http://mac.example.test:1234");
        await Assert.ThrowsAsync<IOException>(() => api.RequestAsync("/message/text", new { message = "Never replay" }));
        Assert.Single(calls);
        calls.Clear(); await api.RestartAsync();
        Assert.Equal(new[] { "/api/v1/ping", "/api/v1/server/restart/hard" }, calls);
        calls.Clear(); failRestart = true;
        await Assert.ThrowsAsync<IOException>(() => api.RestartAsync());
        Assert.Equal(new[] { "/api/v1/ping", "/api/v1/server/restart/hard" }, calls);
    }
    [Fact]
    public async Task CallerCancellationDoesNotTryAnotherAddress()
    {
        using var canceled = new CancellationTokenSource(); var calls = 0;
        using var api = new Api("https://tunnel.example.test", "password", new(), new Handler((_, token) =>
        {
            calls++; canceled.Cancel(); token.ThrowIfCancellationRequested();
            return Task.FromResult(Reply());
        }), "http://mac.example.test:1234");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.RequestAsync("/ping", token: canceled.Token));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task LocalFallbackSyncsHistoryAndManualReconnectKeepsDraftAndSelection()
    {
        await using var fixture = await Fixture.StartAsync();
        var settings = new Preferences { Server = "http://127.0.0.1:1", LocalServer = fixture.Url };
        await fixture.Client.ConnectAsync(settings, "test-password");
        await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Draft(fixture.Guid, "Unsent draft survives recovery");
        await fixture.CommandAsync(new { action = "fail-requests", value = true });
        await fixture.Client.SyncAsync();
        Assert.Equal("offline", fixture.Client.Snapshot().Status);
        Assert.NotEmpty(fixture.Client.Snapshot().Messages);
        await fixture.CommandAsync(new { action = "fail-requests", value = false });
        await fixture.Client.ConnectAsync(settings, "test-password");
        Assert.Equal("connected", fixture.Client.Snapshot().Status);
        Assert.Equal(fixture.Guid, fixture.Client.Active);
        Assert.Equal("Unsent draft survives recovery", fixture.Client.Draft(fixture.Guid));
        await Fixture.UntilAsync(() => fixture.Client.Snapshot().Error.Length == 0);
        await fixture.Client.Connection.RestartAsync();
        var restarts = await fixture.CommandAsync(new { action = "restarts" });
        Assert.Equal(1, restarts.GetArrayLength());
        Assert.Equal("GET", restarts[0].GetProperty("method").GetString());
    }
    [Fact]
    public async Task LocalAddressSurvivesSettingsRoundTripAndLegacyNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ng-recovery-" + Guid.NewGuid());
        try
        {
            var store = new Store(directory);
            await store.SaveAsync("settings.json", new Preferences { Server = "https://tunnel.example.test", LocalServer = "http://mac.example.test:1234" });
            Assert.Equal("http://mac.example.test:1234", store.Load("settings.json", () => new Preferences()).LocalServer);
            Assert.Equal("", Json.Read<Preferences>("{\"localServer\":null}")!.LocalServer);
            Assert.Equal("", Json.Read<Preferences>("{}")!.LocalServer);
        }
        finally { Directory.Delete(directory, true); }
    }
}
