using System.Net;
using System.Text.Json;
using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class OutgoingImagesTests
{
    private static string ImageFile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "test/fixtures/contact.jpg"))) root = root.Parent;
        return Path.Combine(root!.FullName, "test/fixtures/contact.jpg");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    [Fact]
    public async Task MultipleImagesAreSentAsAuthenticatedMultipartAndConfirmedDraftsAreCleared()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync(); await fixture.Client.OpenAsync(fixture.Guid);
        fixture.Client.Draft(fixture.Guid, "Unsent text draft");
        await fixture.Client.QueueImagesAsync(fixture.Guid, [ImageFile(), ImageFile()]);
        var queued = fixture.Client.Images(fixture.Guid); Assert.Equal(2, queued.Count);
        await fixture.Client.SendImagesAsync(fixture.Guid);
        var requests = (await fixture.CommandAsync(new { action = "image-requests" })).EnumerateArray().ToList();
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            Assert.Equal(fixture.Guid, request.GetProperty("chatGuid").GetString());
            Assert.Equal("image/jpeg", request.GetProperty("mime").GetString());
            Assert.Equal("apple-script", request.GetProperty("method").GetString());
            Assert.Equal("contact.jpg", request.GetProperty("name").GetString());
            Assert.Equal(await File.ReadAllBytesAsync(ImageFile()), Convert.FromBase64String(request.GetProperty("bytes").GetString()!));
            Assert.True(Guid.TryParse(request.GetProperty("tempGuid").GetString(), out _));
        }
        Assert.Empty(fixture.Client.Images(fixture.Guid));
        Assert.All(queued, image => Assert.False(File.Exists(fixture.Client.ImagePath(image))));
        Assert.Equal("Unsent text draft", fixture.Client.Draft(fixture.Guid));
        Assert.True(fixture.Client.Snapshot().Messages.Count(message => message.IsFromMe && message.Attachments.Count > 0) >= 2);
        Assert.DoesNotContain("contact.jpg", fixture.Log.Report());
    }
    [Fact]
    public async Task PartialFailureKeepsUnconfirmedImagesAndNeverReplaysConfirmedImages()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await fixture.Client.QueueImagesAsync(fixture.Guid, [ImageFile(), ImageFile(), ImageFile()]);
        var before = fixture.Client.Images(fixture.Guid);
        await fixture.CommandAsync(new { action = "fail-image-at", value = 2 });
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.SendImagesAsync(fixture.Guid));
        Assert.Equal(before.Skip(1), fixture.Client.Images(fixture.Guid));
        Assert.Equal(2, (await fixture.CommandAsync(new { action = "image-requests" })).GetArrayLength());
        var saved = new Store(fixture.DirectoryPath).Load("cache.json", () => new ClientCache());
        Assert.Equal(2, saved.ImageDrafts[fixture.Guid].Count);
        Assert.False(File.Exists(fixture.Client.ImagePath(before[0])));
        Assert.All(before.Skip(1), image => Assert.True(File.Exists(fixture.Client.ImagePath(image))));
    }
    [Fact]
    public async Task ImagesSurviveOriginalRemovalAndRestartAndMoveWithoutChangingTextDrafts()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg"); File.Copy(ImageFile(), source);
        await fixture.Client.QueueImagesAsync("new-conversation", [source]); File.Delete(source);
        using var restored = new MessagingClient(new Store(fixture.DirectoryPath), new(), _ => Task.FromResult(""));
        Assert.Single(restored.Images("new-conversation")); Assert.Empty(restored.Images(fixture.Guid));
        fixture.Client.Draft(fixture.Guid, "An existing text draft");
        await fixture.Client.MoveImagesAsync("new-conversation", fixture.Guid);
        Assert.Empty(fixture.Client.Images("new-conversation")); Assert.Single(fixture.Client.Images(fixture.Guid));
        Assert.Equal("An existing text draft", fixture.Client.Draft(fixture.Guid));
        await fixture.Client.SendImagesAsync(fixture.Guid);
        Assert.Single((await fixture.CommandAsync(new { action = "image-requests" })).EnumerateArray());
    }
    [Fact]
    public async Task RemovingImagesAndDeletingConversationCleansStagedCopies()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await fixture.Client.QueueImagesAsync(fixture.Guid, [ImageFile(), ImageFile()]);
        var queued = fixture.Client.Images(fixture.Guid);
        await fixture.Client.RemoveImageAsync(fixture.Guid, queued[0]);
        Assert.Single(fixture.Client.Images(fixture.Guid)); Assert.False(File.Exists(fixture.Client.ImagePath(queued[0])));
        await fixture.Client.DeleteConversationsAsync([fixture.Guid]);
        Assert.Empty(fixture.Client.Images(fixture.Guid)); Assert.False(File.Exists(fixture.Client.ImagePath(queued[1])));
    }
    [Fact]
    public async Task InvalidFilesAndOversizedBatchesAreRejectedWithoutPartialDrafts()
    {
        await using var fixture = await Fixture.StartAsync();
        var invalid = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            await File.WriteAllTextAsync(invalid, "This is not an image");
            await Assert.ThrowsAsync<IOException>(() => fixture.Client.QueueImagesAsync(fixture.Guid, [ImageFile(), invalid]));
            Assert.Empty(fixture.Client.Images(fixture.Guid));
            await using (var file = File.OpenWrite(invalid)) file.SetLength(ImageFiles.MaxBytes + 1);
            await Assert.ThrowsAsync<IOException>(() => fixture.Client.QueueImagesAsync(fixture.Guid, [invalid]));
            await Assert.ThrowsAsync<IOException>(() => fixture.Client.QueueImagesAsync(fixture.Guid, Enumerable.Repeat(ImageFile(), 11)));
            Assert.Empty(fixture.Client.Images(fixture.Guid));
        }
        finally { File.Delete(invalid); }
    }
    [Fact]
    public async Task AmbiguousUploadResponseNeverRetriesAcrossOriginsOrLeaksFileNames()
    {
        var calls = 0; var log = new Diagnostics();
        using var api = new Api("https://remote.example.test", "private-password", log, new Handler(async request =>
        {
            calls++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("local.example.test", request.RequestUri!.Host);
            Assert.Contains("password=private-password", request.RequestUri.Query);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToList();
            var file = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "attachment");
            Assert.Equal(await File.ReadAllBytesAsync(ImageFile()), await file.ReadAsByteArrayAsync());
            throw new HttpRequestException("An intentionally lost response");
        }), "http://local.example.test:1234");
        var image = ImageFiles.Inspect(ImageFile());
        await Assert.ThrowsAsync<IOException>(() => api.SendImageAsync("synthetic-guid", image, ImageFile(), "private-api", CancellationToken.None));
        Assert.Equal(1, calls); Assert.DoesNotContain("private-password", log.Report()); Assert.DoesNotContain("contact.jpg", log.Report());
    }
}
