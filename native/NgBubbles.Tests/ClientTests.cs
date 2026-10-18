using System.Diagnostics;
using System.Text.Json;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class Fixture : IAsyncDisposable
{
    private readonly Process process;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ngb-test-" + System.Guid.NewGuid());
    public string Url { get; }
    public string Guid { get; }
    public MessagingClient Client { get; }
    public Diagnostics Log { get; } = new();
    private Fixture(Process process, JsonElement metadata)
    {
        this.process = process; Url = metadata.GetProperty("url").GetString()!; Guid = metadata.GetProperty("guid").GetString()!;
        Client = new(new Store(directory), Log, _ => Task.FromResult("synthetic-photo.png"));
    }
    public static async Task<Fixture> StartAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "test", "native-fixture.cjs"))) root = root.Parent;
        if (root == null) throw new IOException("Cannot find test fixture.");
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "test", "native-fixture.cjs"));
        var process = Process.Start(start)!;
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (line == null) throw new IOException(await process.StandardError.ReadToEndAsync());
        return new(process, JsonSerializer.Deserialize<JsonElement>(line));
    }
    public async Task<JsonElement> CommandAsync(object command)
    {
        await process.StandardInput.WriteLineAsync(Json.Write(command)); await process.StandardInput.FlushAsync();
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return JsonSerializer.Deserialize<JsonElement>(line!);
    }
    public Task ConnectAsync() => Client.ConnectAsync(new Preferences { Server = Url }, "test-password");
    public static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Expected state did not arrive."); await Task.Delay(25); }
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await CommandAsync(new { action = "close" }); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { if (!process.HasExited) process.Kill(true); }
        process.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}

public sealed class ClientTests
{
    [Fact]
    public async Task InitialUnreadComesFromServerAndReadingAutomaticallyUpdatesMac()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        fixture.Client.Focused = true;
        await fixture.Client.OpenAsync(fixture.Guid);
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
        var reads = await fixture.CommandAsync(new { action = "reads" });
        Assert.NotEmpty(reads.EnumerateArray());
        await fixture.Client.SyncAsync();
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task FailedReadRetriesAfterSwitchingAwayAndPreservesNewUnread()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await fixture.CommandAsync(new { action = "fail-reads", value = true });
        fixture.Client.Focused = true; await fixture.Client.OpenAsync(fixture.Guid);
        Assert.True(fixture.Client.Cache.ReadStates[fixture.Guid].LocalOnly);
        fixture.Client.Focused = false;
        await fixture.CommandAsync(new { action = "fail-reads", value = false });
        await fixture.Client.SyncAsync();
        await Fixture.UntilAsync(() => !fixture.Client.Cache.ReadStates.TryGetValue(fixture.Guid, out var state) || !state.LocalOnly);
        await fixture.CommandAsync(new { action = "incoming", text = "Fresh incoming", emit = false });
        await fixture.Client.SyncAsync();
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task RealSocketIoDeliversOnceAndStandardSendingPreservesUnicode()
    {
        await using var fixture = await Fixture.StartAsync();
        var count = 0; fixture.Client.Notification += _ => Interlocked.Increment(ref count);
        await fixture.ConnectAsync(); await Task.Delay(400);
        var incoming = await fixture.CommandAsync(new { action = "incoming", text = "Live event 💬" });
        var guid = incoming.GetProperty("guid").GetString();
        await Fixture.UntilAsync(() => fixture.Client.Cache.Histories.GetValueOrDefault(fixture.Guid)?.Any(m => m.Guid == guid) == true);
        await fixture.Client.SyncAsync();
        Assert.Equal(1, count);
        await fixture.Client.SendAsync(fixture.Guid, "Dictated café — 💬");
        var received = await fixture.CommandAsync(new { action = "received" });
        Assert.Equal("Dictated café — 💬", received[0].GetProperty("message").GetString());
        Assert.Equal("apple-script", received[0].GetProperty("method").GetString());
    }
    [Fact]
    public async Task FailedSendIsNotRetriedAndDraftSurvivesRestart()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Draft(fixture.Guid, "Keep this draft");
        await fixture.CommandAsync(new { action = "fail-sends", value = true });
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.SendAsync(fixture.Guid, "Keep this draft"));
        await fixture.Client.SyncAsync();
        Assert.Equal(1, (await fixture.CommandAsync(new { action = "received" })).GetArrayLength());
        Assert.Equal("Keep this draft", fixture.Client.Draft(fixture.Guid));
        var directory = Path.Combine(Path.GetTempPath(), "ngb-restart-" + System.Guid.NewGuid());
        try
        {
            var store = new Store(directory); await store.SaveAsync("cache.json", fixture.Client.Cache);
            using var restored = new MessagingClient(store, new(), _ => Task.FromResult(""));
            Assert.Equal("Keep this draft", restored.Draft(fixture.Guid));
            Assert.Contains(fixture.Guid, restored.Snapshot().Unread);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ContactPhotosAndMissingLiveReadEventRecoverOverRest()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await Fixture.UntilAsync(() => fixture.Client.Snapshot().Contacts.Count == 2);
        Assert.Equal("Jamie Rivera", Model.Name("+15551234567", fixture.Client.Snapshot().Contacts));
        Assert.Equal("synthetic-photo.png", Model.Photo("+1 (555) 123-4567", fixture.Client.Snapshot().ContactPhotos));
        await fixture.CommandAsync(new { action = "read-status", read = true, emit = false });
        await fixture.Client.SyncAsync();
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task DiagnosticsNeverExposeCredentialsOrConversationContent()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        var report = fixture.Log.Report();
        Assert.DoesNotContain("test-password", report); Assert.DoesNotContain("See you", report);
        Assert.DoesNotContain(fixture.Url, report); Assert.DoesNotContain(fixture.Guid, report);
    }
    [Fact]
    public void ValidationAndPhotoAliasesAreSafe()
    {
        Assert.Throws<ArgumentException>(() => Model.NormalizeServer("https://user:pass@host"));
        Assert.Throws<ArgumentException>(() => Model.NormalizeServer("file:///tmp/test"));
        Assert.Throws<ArgumentException>(() => Model.NormalizeServer("https://host/?password=x"));
        Assert.Equal("https://host", Model.NormalizeServer("https://host/api/v1/"));
        Assert.Contains("phone:5551234567", Model.AddressKeys("+1 (555) 123-4567"));
        Assert.DoesNotContain("phone:5551234567", Model.AddressKeys("+44 5551234567"));
        Assert.Equal("email:alex@example.test", Model.AddressKeys("mailto:Alex@Example.Test")[0]);
    }
    [Fact]
    public async Task AtomicCacheRecoversAfterCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ngb-store-" + System.Guid.NewGuid());
        try
        {
            var store = new Store(directory); await store.SaveAsync("cache.json", new ClientCache { Drafts = new() { ["a"] = "hello" } });
            Assert.Equal("hello", store.Load("cache.json", () => new ClientCache()).Drafts["a"]);
            await File.WriteAllTextAsync(Path.Combine(directory, "cache.json"), "broken");
            Assert.Empty(store.Load("cache.json", () => new ClientCache()).Chats);
        }
        finally { Directory.Delete(directory, true); }
    }
}
