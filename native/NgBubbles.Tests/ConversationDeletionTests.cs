using System.Net;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ConversationDeletionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    [Fact]
    public async Task DeleteIsAuthenticatedEncodedAndNeverRetriedAcrossAddresses()
    {
        var calls = new List<Uri>();
        using var api = new Api("https://remote.example.test", "private-password", new(), new Handler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            calls.Add(request.RequestUri!);
            throw new HttpRequestException("Ambiguous response");
        }), "http://local.example.test:1234");
        await Assert.ThrowsAsync<IOException>(() => api.RequestAsync("/chat/" + Uri.EscapeDataString("iMessage;-;name@example.test"), method: HttpMethod.Delete));
        Assert.Single(calls);
        Assert.Contains("password=private-password", calls[0].Query);
        Assert.Contains("iMessage%3B-%3Bname%40example.test", calls[0].AbsoluteUri);
        Assert.Equal("local.example.test", calls[0].Host);
    }
    [Fact]
    public async Task ConfirmedDeletionRemovesServerConversationHistoryDraftAndUnread()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync(); await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Draft(fixture.Guid, "Disposable synthetic draft");
        var old = fixture.Client.Snapshot().Messages[0];
        var historyDirectory = Path.Combine(fixture.DirectoryPath, "history", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fixture.Guid))));
        Assert.True(Directory.Exists(historyDirectory));
        var result = await fixture.Client.DeleteConversationsAsync([fixture.Guid, fixture.Guid]);
        Assert.Equal(new[] { fixture.Guid }, result.Deleted); Assert.Empty(result.Failed);
        Assert.Null(fixture.Client.Active);
        Assert.False(Directory.Exists(historyDirectory));
        var saved = new Store(fixture.DirectoryPath).Load("cache.json", () => new ClientCache());
        Assert.DoesNotContain(saved.Chats, chat => chat.Guid == fixture.Guid);
        Assert.False(saved.Drafts.ContainsKey(fixture.Guid));
        Assert.DoesNotContain(fixture.Client.Snapshot().Chats, chat => chat.Guid == fixture.Guid);
        Assert.False(fixture.Client.Cache.Histories.ContainsKey(fixture.Guid));
        Assert.False(fixture.Client.Cache.HistoryStarts.ContainsKey(fixture.Guid));
        Assert.False(fixture.Client.Cache.ReadStates.ContainsKey(fixture.Guid));
        Assert.Empty(fixture.Client.Draft(fixture.Guid));
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
        fixture.Client.Accept(old with { ChatGuid = fixture.Guid }, false);
        await fixture.Client.SyncAsync();
        Assert.DoesNotContain(fixture.Client.Snapshot().Chats, chat => chat.Guid == fixture.Guid);
        Assert.Single((await fixture.CommandAsync(new { action = "delete-requests" })).EnumerateArray());
    }
    [Fact]
    public async Task PrivateApiMustBeEnabledBeforeAnyDeletionIsSent()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Draft(fixture.Guid, "Keep this draft");
        await fixture.CommandAsync(new { action = "private-api", value = false });
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Client.DeleteConversationsAsync([fixture.Guid]));
        Assert.Contains("Private API", error.Message);
        Assert.Empty((await fixture.CommandAsync(new { action = "delete-requests" })).EnumerateArray());
        Assert.Contains(fixture.Client.Snapshot().Chats, chat => chat.Guid == fixture.Guid);
        Assert.Equal("Keep this draft", fixture.Client.Draft(fixture.Guid));
    }
    [Fact]
    public async Task BulkDeletionKeepsFailedItemsAndDoesNotReplayThem()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        var ids = fixture.Client.Snapshot().Chats.Select(chat => chat.Guid).ToList();
        Assert.Equal(2, ids.Count);
        await fixture.CommandAsync(new { action = "fail-deletes", value = new[] { ids[0] } });
        fixture.Client.Draft(ids[0], "Retain failed draft");
        var result = await fixture.Client.DeleteConversationsAsync(ids);
        Assert.Equal(new[] { ids[1] }, result.Deleted); Assert.Equal(new[] { ids[0] }, result.Failed);
        Assert.Equal(ids[0], Assert.Single(fixture.Client.Snapshot().Chats).Guid);
        Assert.Equal("Retain failed draft", fixture.Client.Draft(ids[0]));
        Assert.Equal(2, (await fixture.CommandAsync(new { action = "delete-requests" })).GetArrayLength());
        Assert.DoesNotContain(ids[0], fixture.Log.Report());
        Assert.DoesNotContain(Uri.EscapeDataString(ids[0]), fixture.Log.Report());
        Assert.Contains("/chat/:id", fixture.Log.Report());
    }
    [Fact]
    public async Task MergedSmsAndImessageSourcesAreDeletedTogetherAndPartialFailureRemainsVisible()
    {
        await using var fixture = await Fixture.StartAsync();
        var sms = (await fixture.CommandAsync(new { action = "transport-history" })).GetString()!;
        await fixture.ConnectAsync();
        await fixture.CommandAsync(new { action = "fail-deletes", value = new[] { sms } });
        fixture.Client.Draft(fixture.Guid, "Keep until complete");
        var result = await fixture.Client.DeleteConversationsAsync([fixture.Guid]);
        Assert.Empty(result.Deleted); Assert.Equal(new[] { fixture.Guid }, result.Failed);
        Assert.Contains(fixture.Client.Snapshot().Chats, chat => chat.Guid == fixture.Guid);
        Assert.Equal("Keep until complete", fixture.Client.Draft(fixture.Guid));
        var calls = (await fixture.CommandAsync(new { action = "delete-requests" })).EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(2, calls.Count); Assert.Contains(fixture.Guid, calls); Assert.Contains(sms, calls);
        await fixture.CommandAsync(new { action = "fail-deletes", value = Array.Empty<string>() });
        result = await fixture.Client.DeleteConversationsAsync([fixture.Guid]);
        Assert.Equal(new[] { fixture.Guid }, result.Deleted);
        Assert.DoesNotContain(fixture.Client.Snapshot().Chats, chat => chat.Guid == fixture.Guid);
        Assert.Equal(3, (await fixture.CommandAsync(new { action = "delete-requests" })).GetArrayLength());
    }
}
