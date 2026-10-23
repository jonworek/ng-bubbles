using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class HomeTests
{
    [Fact]
    public void SummaryUsesLogicalConversationsAndLimitsCardsWithoutLosingCounts()
    {
        var chats = Enumerable.Range(0, 8).Select(i => new Chat { Guid = "chat-" + i,
            LastMessage = new Message { Guid = "message-" + i, DateCreated = i * 1000, Text = "Preview " + i } }).ToList();
        var unread = chats.Take(6).Select(c => c.Guid).ToHashSet();
        var state = new Snapshot(chats, [], null, unread, [], [], [], "offline", "");
        var summary = HomeSummary.Create(state, chats.ToDictionary(c => c.Guid, c => c.Guid == "chat-0" ? "   " : "Draft " + c.Guid));
        Assert.Equal(8, summary.ConversationCount);
        Assert.Equal(6, summary.UnreadCount);
        Assert.Equal(7, summary.DraftCount);
        Assert.Equal(new[] { "chat-7", "chat-6", "chat-5", "chat-4" }, summary.Recent.Select(c => c.Guid));
        Assert.Equal(new[] { "chat-5", "chat-4", "chat-3", "chat-2" }, summary.Unread.Select(c => c.Guid));
        Assert.Equal(3, summary.Drafts.Count);
        Assert.Equal("Draft chat-7", summary.Drafts[0].Text);
        Assert.Equal(6, unread.Count);
    }
    [Fact]
    public void EmptyHomeShowsNoInventedActivity()
    {
        var state = new Snapshot([], [], null, [], [], [], [], "offline", "");
        var summary = HomeSummary.Create(state, new Dictionary<string, string>());
        Assert.Equal(0, summary.ConversationCount);
        Assert.Equal(0, summary.UnreadCount);
        Assert.Equal(0, summary.DraftCount);
        Assert.Empty(summary.Unread); Assert.Empty(summary.Recent); Assert.Empty(summary.Drafts);
    }
    [Fact]
    public async Task HomePreservesDraftsAndDoesNotReadIncomingMessages()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        fixture.Client.Focused = true;
        await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Draft(fixture.Guid, "Unsent text");
        fixture.Client.Home();
        Assert.Null(fixture.Client.Active);
        Assert.Empty(fixture.Client.Snapshot().Messages);
        Assert.Equal("Unsent text", fixture.Client.Draft(fixture.Guid));
        fixture.Client.Focused = true;
        var readsBefore = (await fixture.CommandAsync(new { action = "reads" })).GetArrayLength();
        await fixture.CommandAsync(new { action = "incoming", text = "Arrived on Home" });
        await Fixture.UntilAsync(() => fixture.Client.Snapshot().Unread.Contains(fixture.Guid));
        Assert.Null(fixture.Client.Active);
        Assert.Equal(readsBefore, (await fixture.CommandAsync(new { action = "reads" })).GetArrayLength());
        var summary = HomeSummary.Create(fixture.Client.Snapshot(), fixture.Client.Drafts());
        Assert.Contains(summary.Unread, c => c.Guid == fixture.Guid);
        Assert.Contains(summary.Drafts, d => d.Chat.Guid == fixture.Guid && d.Text == "Unsent text");
        fixture.Client.Focused = false;
        await fixture.Client.OpenAsync(fixture.Guid);
        Assert.Equal(fixture.Guid, fixture.Client.Active);
        Assert.Equal("Unsent text", fixture.Client.Draft(fixture.Guid));
    }
}
