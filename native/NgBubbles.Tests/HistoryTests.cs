using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class HistoryTests
{
    [Fact]
    public async Task HistoryBeyondOneThousandMessagesSurvivesRestartAndLoadsOffline()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CommandAsync(new { action = "history-seed", count = 1105 });
        var directory = Path.Combine(Path.GetTempPath(), "ngb-history-" + Guid.NewGuid());
        try
        {
            var store = new Store(directory);
            using (var client = new MessagingClient(store, new(), _ => Task.FromResult("")))
            {
                await client.ConnectAsync(new Preferences { Server = fixture.Url }, "test-password");
                await client.OpenAsync(fixture.Guid);
                for (var page = 0; page < 20 && client.CanLoadOlder(fixture.Guid); page++)
                    await client.OpenAsync(fixture.Guid, client.Snapshot().Messages[0].DateCreated);
                Assert.Equal(1105, client.Snapshot().Messages.Count);
                Assert.False(client.CanLoadOlder(fixture.Guid));
                for (var i = 0; i < 25; i++) client.Accept(new Message
                {
                    Guid = "newer-" + i, ChatGuid = fixture.Guid, Text = "Newer cached message", IsFromMe = true,
                    DateCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + i
                }, notify: false);
                await client.SaveAsync();
                Assert.Equal(1130, client.Snapshot().Messages.Count);
            }
            Assert.Equal(1000, store.Load("cache.json", () => new ClientCache()).Histories[fixture.Guid].Count);
            var requests = (await fixture.CommandAsync(new { action = "history-requests" })).GetArrayLength();
            using var restored = new MessagingClient(store, new(), _ => Task.FromResult(""));
            await restored.OpenAsync(fixture.Guid);
            Assert.Equal(1000, restored.Snapshot().Messages.Count);
            for (var page = 0; page < 20 && restored.CanLoadOlder(fixture.Guid); page++)
                await restored.OpenAsync(fixture.Guid, restored.Snapshot().Messages[0].DateCreated);
            Assert.False(restored.CanLoadOlder(fixture.Guid));
            Assert.Equal(1130, restored.Snapshot().Messages.Count);
            Assert.Equal("history-000000", restored.Snapshot().Messages[0].Guid);
            Assert.Equal(1130, restored.Snapshot().Messages.Select(message => message.Guid).Distinct().Count());
            Assert.Equal(requests, (await fixture.CommandAsync(new { action = "history-requests" })).GetArrayLength());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ConcurrentOlderRequestsFetchOnePageAndRememberTheEndOfHistory()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CommandAsync(new { action = "history-seed", count = 200 });
        await fixture.ConnectAsync();
        await fixture.Client.OpenAsync(fixture.Guid);
        var before = fixture.Client.Snapshot().Messages[0].DateCreated;
        await Task.WhenAll(fixture.Client.OpenAsync(fixture.Guid, before), fixture.Client.OpenAsync(fixture.Guid, before));
        Assert.Equal(200, fixture.Client.Snapshot().Messages.Count);
        var oldest = fixture.Client.Snapshot().Messages[0].DateCreated;
        await fixture.Client.OpenAsync(fixture.Guid, oldest);
        await fixture.Client.OpenAsync(fixture.Guid, oldest);
        Assert.False(fixture.Client.CanLoadOlder(fixture.Guid));
        var requests = await fixture.CommandAsync(new { action = "history-requests" });
        Assert.Equal(2, requests.EnumerateArray().Count(request => request.GetProperty("before").ValueKind == System.Text.Json.JsonValueKind.Number));
    }
    [Fact]
    public async Task FailedOlderPageRemainsRetryableAndDoesNotChangeVisibleHistory()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CommandAsync(new { action = "history-seed", count = 230 });
        await fixture.ConnectAsync();
        await fixture.Client.OpenAsync(fixture.Guid);
        var before = fixture.Client.Snapshot().Messages[0].DateCreated;
        await fixture.CommandAsync(new { action = "fail-history", value = true });
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.OpenAsync(fixture.Guid, before));
        Assert.Equal(100, fixture.Client.Snapshot().Messages.Count);
        Assert.True(fixture.Client.CanLoadOlder(fixture.Guid));
        await fixture.CommandAsync(new { action = "fail-history", value = false });
        await fixture.Client.OpenAsync(fixture.Guid, before);
        await fixture.Client.OpenAsync(fixture.Guid, fixture.Client.Snapshot().Messages[0].DateCreated);
        Assert.Equal(230, fixture.Client.Snapshot().Messages.Count);
        Assert.False(fixture.Client.CanLoadOlder(fixture.Guid));
    }
}
