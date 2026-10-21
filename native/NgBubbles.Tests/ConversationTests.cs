using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ConversationTests
{
    [Fact]
    public async Task FallbackThreadsShareHistoryReadStateDraftsAndSendRoute()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        fixture.Client.Draft(fixture.Guid, "Preserve my draft");
        var sms = (await fixture.CommandAsync(new { action = "transport-history" })).GetString()!;
        await fixture.Client.SyncAsync();
        Assert.Equal(2, fixture.Client.Snapshot().Chats.Count);
        Assert.Single(fixture.Client.Snapshot().Chats, c => c.Participants.Any(p => p.Address.Contains("555")));
        fixture.Client.Focused = true;
        await fixture.Client.OpenAsync(sms);
        Assert.Equal(fixture.Guid, fixture.Client.Active);
        Assert.Equal("Preserve my draft", fixture.Client.Draft(sms));
        for (var page = 0; page < 10 && fixture.Client.CanLoadOlder(sms); page++)
            await fixture.Client.OpenAsync(sms, fixture.Client.Snapshot().Messages[0].DateCreated);
        var messages = fixture.Client.Snapshot().Messages;
        Assert.Equal(502, messages.Count);
        Assert.Equal(502, messages.Select(m => m.Guid).Distinct().Count());
        Assert.Contains(messages, m => Model.IsTextMessage(m));
        Assert.Contains(messages, m => Model.MessageService(m) == "iMessage");
        var reads = (await fixture.CommandAsync(new { action = "reads" })).EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains(fixture.Guid, reads);
        Assert.Contains(sms, reads);
        await fixture.Client.SyncAsync();
        Assert.Empty(fixture.Client.Snapshot().Unread);
        await fixture.CommandAsync(new { action = "fail-sends", value = true });
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.SendAsync(fixture.Guid, "Retry me"));
        Assert.Equal("Preserve my draft", fixture.Client.Draft(sms));
        await fixture.CommandAsync(new { action = "fail-sends", value = false });
        await fixture.Client.SendAsync(fixture.Guid, "Retry me");
        var received = (await fixture.CommandAsync(new { action = "received" })).EnumerateArray().ToList();
        Assert.Equal(2, received.Count);
        Assert.All(received, send => Assert.Equal(sms, send.GetProperty("chatGuid").GetString()));
        Assert.Equal(2, fixture.Client.Snapshot().Chats.Count);
        fixture.Client.Draft(sms, "");
        Assert.Equal("", fixture.Client.Draft(fixture.Guid));
    }
    [Fact]
    public async Task DistinctRecipientsAndGroupsStaySeparateAndMappingSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ngb-conversations-" + Guid.NewGuid());
        try
        {
            var store = new Store(directory);
            var cache = new ClientCache { Chats = [
                new() { Guid = "SMS;-;5551234567", DisplayName = "Same name", Participants = [new("5551234567")] },
                new() { Guid = "iMessage;-;+15551234567", DisplayName = "Same name", Participants = [new("+15551234567")] },
                new() { Guid = "SMS;-;+445551234567", DisplayName = "Same name", Participants = [new("+445551234567")] },
                new() { Guid = "iMessage;-;person@example.test", DisplayName = "Same name", Participants = [new("person@example.test")] },
                new() { Guid = "iMessage;+;one", Participants = [new("+15551234567")] },
                new() { Guid = "SMS;+;two", Participants = [new("+15551234567")] }
            ] };
            await store.SaveAsync("cache.json", cache);
            using (var client = new MessagingClient(store, new(), _ => Task.FromResult("")))
            {
                Assert.Equal(5, client.Snapshot().Chats.Count);
                client.Select("iMessage;-;+15551234567");
                Assert.Equal("SMS;-;5551234567", client.Active);
                client.Draft(client.Active!, "Draft across update");
                await client.SaveAsync();
            }
            using var restored = new MessagingClient(store, new(), _ => Task.FromResult(""));
            restored.Cache.Chats.Reverse();
            restored.Select("iMessage;-;+15551234567");
            Assert.Equal("SMS;-;5551234567", restored.Active);
            Assert.Equal("Draft across update", restored.Draft(restored.Active!));
            Assert.Equal(5, restored.Snapshot().Chats.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("SMS", "iMessage;-;+15551234567", true)]
    [InlineData("iMessage", "SMS;-;+15551234567", false)]
    [InlineData(null, "SMS;-;+15551234567", true)]
    [InlineData(null, "RCS;-;+15551234567", true)]
    [InlineData(null, "iMessage;-;+15551234567", false)]
    public void MessageTransportTakesPriorityOverThread(string? service, string guid, bool green)
    {
        var message = new Message { Service = service, ChatGuid = guid };
        Assert.Equal(green, Model.IsTextMessage(message));
        Assert.Null(Json.Read<Message>("""{"error":null}""")!.Error);
    }
    [Fact]
    public async Task ContactRefreshFindsNewServerContactsWithoutReconnect()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        await fixture.CommandAsync(new { action = "new-contact" });
        await fixture.Client.RefreshPhotosAsync();
        await Fixture.UntilAsync(() => fixture.Client.Snapshot().Contacts.ContainsKey(Model.AddressKey("+15559876543")));
        Assert.Equal("Morgan Taylor", Model.Name("+15559876543", fixture.Client.Snapshot().Contacts));
    }
}
