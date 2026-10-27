using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ConversationFilterTests
{
    [Fact]
    public void FiltersCombineWithSearchAndPreserveConversationOrder()
    {
        var direct = new Chat { Guid = "direct", Participants = [new("alex@example.test")] };
        var group = new Chat { Guid = "group", DisplayName = "Weekend plans", Participants = [new("alex@example.test"), new("sam@example.test")] };
        var read = new Chat { Guid = "read", Style = 43, DisplayName = "Read group" };
        var state = new Snapshot([direct, group, read], [], "direct", ["direct", "group"], new() { ["alex@example.test"] = "Alex" }, [], [], "connected", "");
        Assert.Equal(new[] { "direct", "group", "read" }, ConversationFilters.Apply(state, "", false, false).Select(chat => chat.Guid));
        Assert.Equal(new[] { "direct", "group" }, ConversationFilters.Apply(state, "", true, false).Select(chat => chat.Guid));
        Assert.Equal(new[] { "group", "read" }, ConversationFilters.Apply(state, "", false, true).Select(chat => chat.Guid));
        Assert.Equal("group", Assert.Single(ConversationFilters.Apply(state, "WEEKEND", true, true)).Guid);
        Assert.Equal("group", Assert.Single(ConversationFilters.Apply(state, "SAM@EXAMPLE", true, true)).Guid);
        Assert.Equal("direct", Assert.Single(ConversationFilters.Apply(state, "ALEX", true, false).Where(chat => !chat.IsGroup)).Guid);
        Assert.Empty(ConversationFilters.Apply(state, "missing", true, true));
        Assert.Equal(3, state.Chats.Count); Assert.Equal(2, state.Unread.Count); Assert.Equal("direct", state.Active);
    }
    [Fact]
    public void GroupsRecognizeServerStyleAndGuidEvenWithIncompleteParticipants()
    {
        var style = new Chat { Guid = "style", Style = 43 };
        var guid = new Chat { Guid = "iMessage;+;group" };
        var direct = new Chat { Guid = "direct", DisplayName = "A group in name only", Participants = [new("one@example.test")] };
        var state = new Snapshot([style, guid, direct], [], null, ["style", "guid"], [], [], [], "offline", "");
        Assert.Equal(new[] { style, guid }, ConversationFilters.Apply(state, "", false, true));
    }
    [Fact]
    public async Task UnreadResultsFollowServerChangesWithoutChangingSelectionOrDrafts()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        fixture.Client.Focused = false;
        fixture.Client.Draft(fixture.Guid, "Saved draft");
        await fixture.CommandAsync(new { action = "incoming", text = "New unread conversation" });
        await Fixture.UntilAsync(() => fixture.Client.Snapshot().Unread.Contains(fixture.Guid));
        Assert.Contains(ConversationFilters.Apply(fixture.Client.Snapshot(), "", true, false), chat => chat.Guid == fixture.Guid);
        await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Focused = true;
        await fixture.Client.ReadAsync(fixture.Guid, force: true);
        await Fixture.UntilAsync(() => !fixture.Client.Snapshot().Unread.Contains(fixture.Guid));
        Assert.DoesNotContain(ConversationFilters.Apply(fixture.Client.Snapshot(), "", true, false), chat => chat.Guid == fixture.Guid);
        Assert.Equal(fixture.Guid, fixture.Client.Active);
        Assert.Equal("Saved draft", fixture.Client.Draft(fixture.Guid));
        Assert.Contains(fixture.Guid, (await fixture.CommandAsync(new { action = "reads" })).EnumerateArray().Select(item => item.GetString()));
    }
}
