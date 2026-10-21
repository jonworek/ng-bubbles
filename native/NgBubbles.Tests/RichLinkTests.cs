using System.Text.Json;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class RichLinkTests
{
    [Fact]
    public void ArchivedLinkMetadataResolvesTitleUrlAndImageReference()
    {
        var message = Json.Read<Message>("""{"balloonBundleId":"com.apple.messages.URLBalloonProvider","attachments":[{"transferName":"opaque.pluginPayloadAttachment"}],"payloadData":[{"$objects":["$null",{"richLinkMetadata":{"UID":2}},{"title":{"UID":3},"originalURL":{"UID":4},"image":{"UID":6}},"Article title",{"NS.relative":{"UID":5}},"https://example.test/article",{"richLinkImageAttachmentSubstituteIndex":0}]}]}""")!;
        var link = RichLinks.Read(message)!;
        Assert.Equal("https://example.test/article", link.Url);
        Assert.Equal("Article title", link.Title);
        Assert.Equal("example.test", link.Site);
        Assert.Equal(0, link.ImageIndex);
    }
    [Fact]
    public void MalformedOrCyclicMetadataKeepsTheUsableTextLink()
    {
        foreach (var payload in new[] { "[null,7,{}]", "[{\"$objects\":[{\"richLinkMetadata\":{\"UID\":0}}]}]", "[{\"$objects\":[{\"richLinkMetadata\":{\"UID\":999}}]}]" })
        {
            var message = Json.Read<Message>("{\"text\":\"https://example.test/article\",\"balloonBundleId\":\"com.apple.messages.URLBalloonProvider\",\"payloadData\":" + payload + "}")!;
            Assert.Equal("https://example.test/article", RichLinks.Read(message)!.Url);
        }
        Assert.Null(RichLinks.SafeUrl("javascript:alert(1)"));
        Assert.Null(RichLinks.SafeUrl("file:///private/example"));
        Assert.Null(RichLinks.SafeUrl("https://user:password@example.test"));
    }
    [Fact]
    public void OrdinaryImagesAndAppAttachmentsAreNotHiddenAsRichLinks()
    {
        Assert.Null(RichLinks.Read(new Message { Text = "Photo", Attachments = [new() { TransferName = "photo.jpg" }] }));
        Assert.Null(RichLinks.Read(new Message { Text = "Shared game", Attachments = [new() { TransferName = "game.pluginPayloadAttachment" }] }));
        Assert.NotNull(RichLinks.Read(new Message { Text = "See https://example.test/article", Attachments = [new() { TransferName = "preview.pluginPayloadAttachment" }] }));
    }
    [Fact]
    public void MetadataSurvivesHistoryRefreshAndCacheSerialization()
    {
        var enriched = Json.Read<Message>("""{"guid":"one","text":"https://example.test","payloadData":[{"$objects":[]}]}""")!;
        var merged = Model.Merge([enriched], [enriched with { PayloadData = null, DateDelivered = 123 }]).Single();
        Assert.NotNull(Json.Read<Message>(Json.Write(merged))!.PayloadData);
        Assert.Equal(123, merged.DateDelivered);
    }
    [Fact]
    public async Task CachedHistoryCanFetchAndRetainRichLinkDetails()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ConnectAsync();
        var message = (await fixture.CommandAsync(new { action = "rich-link" })).Deserialize<Message>(Json.Options)!;
        fixture.Client.Accept(message, false);
        var enriched = await fixture.Client.LinkDetailsAsync(message);
        Assert.Equal("Article title", RichLinks.Read(enriched)!.Title);
        Assert.DoesNotContain(message.Guid, fixture.Log.Report());
        await fixture.Client.SyncAsync();
        await fixture.Client.OpenAsync(fixture.Guid);
        var restored = fixture.Client.Snapshot().Messages.Single(m => m.Guid == message.Guid);
        Assert.Equal("Article title", RichLinks.Read(restored)!.Title);
    }
}
