using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class MessageReactionsTests
{
    private static Message Original(string id = "target", string text = "Weekend plans") => new() { Guid = id, Text = text, DateCreated = 1, IsFromMe = true };
    private static Message Reaction(string id, int type = 2001, string target = "p:0/target", string sender = "alex@example.test", long date = 2) => new()
    { Guid = id, AssociatedMessageGuid = target, AssociatedMessageType = type, Handle = new(sender), DateCreated = date };
    [Fact]
    public void MetadataAttachesAllStandardAndCustomReactionsToTheReferencedMessage()
    {
        var emojis = new[] { "❤️", "👍", "👎", "😂", "‼️", "❓", "🤦" };
        for (var index = 0; index < emojis.Length; index++)
        {
            var reaction = Reaction("reaction", 2000 + index) with { AssociatedMessageEmoji = index == 6 ? emojis[index] : null };
            var shown = Assert.Single(MessageReactions.Apply([Original(), reaction]));
            Assert.Equal("target", shown.Guid); Assert.Equal("Weekend plans", shown.Text);
            Assert.Equal(emojis[index], Assert.Single(shown.Reactions).Emoji);
            Assert.Equal("alex@example.test", shown.Reactions[0].Sender);
        }
        Assert.Single(Assert.Single(MessageReactions.Apply([Original(), Reaction("bare", target: "target")])).Reactions);
        Assert.Single(Assert.Single(MessageReactions.Apply([Original(), Reaction("bubble", target: "bp:target")])).Reactions);
    }
    [Fact]
    public void ChangesReplaceOneSenderAndRemovalDoesNotEraseOtherSendersOrNewerEmoji()
    {
        var original = Original();
        var like = Reaction("like");
        var other = Reaction("other", sender: "sam@example.test", date: 3);
        var love = Reaction("love", 2000, date: 4);
        var staleRemoval = Reaction("old-removal", 3001, date: 5);
        var shown = Assert.Single(MessageReactions.Apply([original, like, other, love, staleRemoval]));
        Assert.Equal(2, shown.Reactions.Count);
        Assert.Contains(shown.Reactions, reaction => reaction.Emoji == "❤️" && reaction.Sender == "alex@example.test");
        shown = Assert.Single(MessageReactions.Apply([original, like, other, love, staleRemoval, Reaction("remove-love", 3000, date: 6)]));
        Assert.Equal("sam@example.test", Assert.Single(shown.Reactions).Sender);
        Assert.Empty(Assert.Single(MessageReactions.Apply([original, like, Reaction("remove", 3001, date: 3)])).Reactions);
    }
    [Fact]
    public void TextOnlySummariesAttachOnlyWhenTheQuotedOriginalIsUnambiguous()
    {
        foreach (var (text, emoji) in new[] { ("Reacted 🤦 to “Weekend plans”", "🤦"), ("Liked “Weekend plans”", "👍"),
            ("Loved \"Weekend plans\"", "❤️"), ("Laughed at “Weekend plans”", "😂"), ("Emphasized “Weekend plans”", "‼️"),
            ("👍 to “Weekend plans”", "👍"), ("Reacted 👨‍👩‍👧 to “Weekend plans”", "👨‍👩‍👧") })
        {
            var reaction = new Message { Guid = "summary", Text = text, Handle = new("alex@example.test"), DateCreated = 10 };
            Assert.Equal(emoji, Assert.Single(Assert.Single(MessageReactions.Apply([Original(), reaction])).Reactions).Emoji);
            Assert.Equal(3, MessageReactions.Apply([Original(), Original("duplicate"), reaction]).Count);
        }
        var added = new Message { Guid = "a", Text = "Reacted 🤦 to “Weekend plans”", Handle = new("alex@example.test"), DateCreated = 2 };
        var removed = added with { Guid = "b", Text = "Removed 🤦 from “Weekend plans”", DateCreated = 3 };
        Assert.Empty(Assert.Single(MessageReactions.Apply([Original(), added, removed])).Reactions);
    }
    [Fact]
    public void UnloadedTargetsRemainReadableAndResolveWhenAnOlderPageArrives()
    {
        var reaction = Reaction("later") with { Text = "Liked “Weekend plans”" };
        Assert.Equal("later", Assert.Single(MessageReactions.Apply([reaction])).Guid);
        var raw = Json.Read<List<Message>>(Json.Write(new[] { reaction, Original() }))!;
        var original = Assert.Single(MessageReactions.Apply(raw));
        Assert.Equal("target", original.Guid); Assert.Single(original.Reactions);
        Assert.Equal(2, raw.Count); Assert.Empty(raw[1].Reactions);
        Assert.DoesNotContain("reactions", Json.Write(original), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void UnknownTypesAndMissingMetadataTargetsDoNotAttachToMatchingQuotedText()
    {
        var unknown = Reaction("unknown", 1000) with { Text = "Liked “Weekend plans”" };
        var missing = Reaction("missing", target: "p:0/not-loaded") with { Text = "Liked “Weekend plans”" };
        Assert.Equal(3, MessageReactions.Apply([Original(), unknown, missing]).Count);
        Assert.Empty(MessageReactions.Apply([Original(), unknown, missing])[0].Reactions);
        var ordinary = new Message { Guid = "text", Text = "Reacted angrily to “Weekend plans”", DateCreated = 2 };
        Assert.Equal(2, MessageReactions.Apply([Original(), ordinary]).Count);
    }
    [Fact]
    public void PartialMessageUpdatesRetainReactionMetadataAndCustomEmoji()
    {
        var reaction = Reaction("event", 2006) with { AssociatedMessageEmoji = "🤦" };
        var patch = new Message { Guid = "event", DateCreated = 2, IsRead = true };
        var merged = Assert.Single(Model.Merge([reaction], [patch]));
        Assert.Equal("p:0/target", merged.AssociatedMessageGuid); Assert.Equal(2006, merged.AssociatedMessageType);
        Assert.Equal("🤦", merged.AssociatedMessageEmoji);
        Assert.Equal("🤦", Assert.Single(Assert.Single(MessageReactions.Apply([Original(), merged])).Reactions).Emoji);
    }
    [Fact]
    public async Task LiveMessagesUpdateReactionBadgesWithoutLosingRawCachedEvents()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync(); await fixture.Client.OpenAsync(fixture.Guid);
        var target = fixture.Client.Snapshot().Messages.First(message => !string.IsNullOrWhiteSpace(message.Text));
        var added = Reaction("live-reaction", target: "p:0/" + target.Guid, date: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) with { ChatGuid = fixture.Guid };
        fixture.Client.Accept(added, notify: false, updateUnread: false);
        var shown = fixture.Client.Snapshot().Messages;
        Assert.DoesNotContain(shown, message => message.Guid == added.Guid);
        Assert.Equal("👍", Assert.Single(shown.Single(message => message.Guid == target.Guid).Reactions).Emoji);
        Assert.Contains(fixture.Client.Cache.Histories[fixture.Guid], message => message.Guid == added.Guid);
        fixture.Client.Accept(added with { Guid = "remove-live", AssociatedMessageType = 3001, DateCreated = added.DateCreated + 1 }, notify: false, updateUnread: false);
        Assert.Empty(fixture.Client.Snapshot().Messages.Single(message => message.Guid == target.Guid).Reactions);
    }
}
