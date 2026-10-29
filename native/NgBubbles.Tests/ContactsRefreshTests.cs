using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class ContactsRefreshTests
{
    [Fact]
    public async Task ManualRefreshPicksUpNewServerContactsAndRecordsSuccessfulTime()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await Fixture.UntilAsync(() => fixture.Client.ContactsRefreshedAt != null);
        Assert.Equal(TimeSpan.FromHours(1), MessagingClient.ContactRefreshInterval);
        var before = fixture.Client.ContactsRefreshedAt;
        await fixture.CommandAsync(new { action = "new-contact" });
        await fixture.Client.RefreshContactsAsync();
        Assert.Equal("Morgan Taylor", fixture.Client.Snapshot().Contacts[Model.AddressKey("+15559876543")]);
        Assert.True(fixture.Client.ContactsRefreshedAt >= before);
    }
    [Fact]
    public async Task ManualRefreshFailureReportsErrorWithoutDiscardingSavedContactsOrPhotos()
    {
        await using var fixture = await Fixture.StartAsync(); await fixture.ConnectAsync();
        await Fixture.UntilAsync(() => fixture.Client.ContactsRefreshedAt != null);
        var contacts = fixture.Client.Snapshot().Contacts;
        var photos = fixture.Client.Snapshot().ContactPhotos;
        var refreshed = fixture.Client.ContactsRefreshedAt;
        await fixture.CommandAsync(new { action = "fail-requests", value = true });
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.RefreshContactsAsync());
        Assert.Equal(contacts, fixture.Client.Snapshot().Contacts);
        Assert.Equal(photos, fixture.Client.Snapshot().ContactPhotos);
        Assert.Equal(refreshed, fixture.Client.ContactsRefreshedAt);
    }
}
