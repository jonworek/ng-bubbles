using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ResponseFormatTests
{
    [Fact]
    public void ReactionTypesAcceptServerNamesNumbersNumericStringsAndNull()
    {
        var names = new[] { "love", "like", "dislike", "laugh", "emphasize", "question" };
        for (var index = 0; index < names.Length; index++)
        {
            foreach (var (name, code) in new[] { (names[index], 2000 + index), ("-" + names[index], 3000 + index) })
            {
                foreach (var value in new[] { Json.Write(name), Json.Write(code), Json.Write(code.ToString()) })
                {
                    var message = Json.Read<Message>("{\"associatedMessageType\":" + value + "}")!;
                    Assert.Equal(code, message.AssociatedMessageType);
                    Assert.Equal(code, Json.Read<Message>(Json.Write(message))!.AssociatedMessageType);
                }
            }
        }
        Assert.Null(Json.Read<Message>("""{"associatedMessageType":null}""")!.AssociatedMessageType);
        Assert.Null(Json.Read<Message>("{}")!.AssociatedMessageType);
        Assert.Equal(1000, Json.Read<Message>("""{"associatedMessageType":"sticker"}""")!.AssociatedMessageType);
    }
    [Fact]
    public void NestedLatestMessagesCustomEmojiAndUnknownNamesDoNotBreakParsing()
    {
        var chats = Json.Read<List<Chat>>("""[{"guid":"synthetic-chat","lastMessage":{"guid":"reaction","associatedMessageGuid":"p:0/target","associatedMessageType":"love"}}]""")!;
        Assert.Equal(2000, chats[0].LastMessage!.AssociatedMessageType);
        var target = new Message { Guid = "target", Text = "Weekend plans", DateCreated = 1 };
        var custom = Json.Read<Message>("""{"guid":"custom","associatedMessageGuid":"p:0/target","associatedMessageType":"2006","associatedMessageEmoji":"🤦","dateCreated":2}""")!;
        Assert.Equal("🤦", Assert.Single(Assert.Single(MessageReactions.Apply([target, custom])).Reactions).Emoji);
        var removed = custom with { Guid = "removed", IsFromMe = true, AssociatedMessageType = 3006, DateCreated = 3 };
        Assert.Empty(Assert.Single(MessageReactions.Apply([target, custom with { IsFromMe = true }, removed])).Reactions);
        var unknown = Json.Read<Message>("""{"guid":"unknown","text":"Liked “Weekend plans”","associatedMessageType":"future-reaction","dateCreated":2}""")!;
        Assert.Equal(-1, unknown.AssociatedMessageType);
        Assert.Equal(2, MessageReactions.Apply([target, unknown]).Count);
        Assert.Empty(MessageReactions.Apply([target, unknown])[0].Reactions);
    }
    [Fact]
    public async Task NamedReactionInConversationListSyncsUnreadAndDisplaysBadge()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CommandAsync(new { action = "reaction-format", type = "love" });
        await fixture.ConnectAsync();
        Assert.Equal("connected", fixture.Client.Snapshot().Status);
        Assert.Contains(fixture.Guid, fixture.Client.Snapshot().Unread);
        await fixture.Client.OpenAsync(fixture.Guid);
        var target = fixture.Client.Snapshot().Messages.Single(message => message.Guid == "reaction-target");
        Assert.Equal("❤️", Assert.Single(target.Reactions).Emoji);
        Assert.DoesNotContain(fixture.Client.Snapshot().Messages, message => message.Guid == "named-reaction");
        Assert.DoesNotContain("sync-failed", fixture.Log.Report());
    }
    [Fact]
    public async Task InvalidConversationDataRetainsCacheAndReportsDataErrorInsteadOfOffline()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        var before = fixture.Client.Snapshot().Chats.Select(chat => chat.Guid).ToList();
        await fixture.CommandAsync(new { action = "malformed-message", value = true });
        await fixture.Client.SyncAsync();
        var failed = fixture.Client.Snapshot();
        Assert.Equal("connected", failed.Status);
        Assert.Contains("conversation data could not be read", failed.Error);
        Assert.Equal(before, failed.Chats.Select(chat => chat.Guid));
        var report = fixture.Log.Report();
        Assert.Contains("response-format-failed", report);
        Assert.Contains("DateCreated", report);
        Assert.DoesNotContain("invalid-date", report);
        Assert.DoesNotContain("See you this weekend", report);
        await fixture.CommandAsync(new { action = "malformed-message", value = false });
        await fixture.Client.SyncAsync();
        Assert.Equal("connected", fixture.Client.Snapshot().Status);
        Assert.DoesNotContain("conversation data could not be read", fixture.Client.Snapshot().Error);
    }
}
