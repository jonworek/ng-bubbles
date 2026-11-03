using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class MarkUnreadTests
{
    [Fact]
    public async Task ReopeningWhileMarkUnreadIsStillInFlightReadsAfterTheUnreadMutation()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Focused = true; await fixture.Client.OpenAsync(fixture.Guid);
        await fixture.CommandAsync(new { action = "delay-unread", value = 200 });
        var marking = fixture.Client.MarkUnreadAsync(fixture.Guid);
        await Fixture.UntilAsync(() => fixture.Client.Active == null && fixture.Client.Snapshot().Unread.Contains(fixture.Guid));
        var opening = fixture.Client.OpenAsync(fixture.Guid);
        await Task.WhenAll(marking, opening);
        await fixture.Client.SyncAsync();
        Assert.Equal(fixture.Guid, fixture.Client.Active);
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task MarkingOpenConversationUnreadUpdatesServerAndStaysUnreadUntilReopened()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Focused = true; await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Draft(fixture.Guid, "Keep my draft");
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
        await fixture.Client.MarkUnreadAsync(fixture.Guid);
        Assert.Null(fixture.Client.Active); Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        Assert.Equal("Keep my draft", fixture.Client.Draft(fixture.Guid));
        Assert.Contains((await fixture.CommandAsync(new { action = "unread-requests" })).EnumerateArray(), item => item.GetString() == fixture.Guid);
        await fixture.Client.SyncAsync();
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        await fixture.Client.OpenAsync(fixture.Guid);
        Assert.DoesNotContain(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task PrivateApiUnavailableKeepsLocalMarkerAcrossSyncAndRestart()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Focused = true; await fixture.Client.OpenAsync(fixture.Guid);
        await fixture.CommandAsync(new { action = "private-api", value = false });
        await fixture.Client.MarkUnreadAsync(fixture.Guid); await fixture.Client.SyncAsync();
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        Assert.Contains("Private API", fixture.Client.Snapshot().Error);
        Assert.Empty((await fixture.CommandAsync(new { action = "unread-requests" })).EnumerateArray());
        using var restored = new MessagingClient(new Store(fixture.DirectoryPath), new(), _ => Task.FromResult(""));
        Assert.Contains(fixture.Guid, restored.Snapshot().Unread);
    }
    [Fact]
    public async Task ServerFailureDoesNotReplayRequestOrLoseLocalUnreadState()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        fixture.Client.Focused = true; await fixture.Client.OpenAsync(fixture.Guid);
        await fixture.CommandAsync(new { action = "fail-reads", value = true });
        await fixture.Client.MarkUnreadAsync(fixture.Guid);
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        Assert.Contains("not confirmed", fixture.Client.Snapshot().Error);
        Assert.Single((await fixture.CommandAsync(new { action = "unread-requests" })).EnumerateArray());
        await fixture.Client.SyncAsync();
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
    [Fact]
    public async Task MergedConversationMarksAllUnderlyingTransportsUnread()
    {
        await using var fixture = await Fixture.StartAsync();
        var sms = (await fixture.CommandAsync(new { action = "transport-history" })).GetString()!;
        await fixture.ConnectAsync();
        await fixture.Client.MarkUnreadAsync(fixture.Guid);
        var calls = (await fixture.CommandAsync(new { action = "unread-requests" })).EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(2, calls.Count); Assert.Contains(sms, calls); Assert.Contains(fixture.Guid, calls);
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
    }
}
