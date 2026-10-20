using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NgBubbles.Core;

namespace NgBubbles.Windows;

public sealed class Notifications : IDisposable
{
    private readonly LocalData data;
    private readonly NotificationAvatar avatars;
    public event Action<string?>? Activated;
    private bool registered;
    public Notifications(LocalData data)
    {
        this.data = data;
        avatars = new(data);
        try
        {
            AppNotificationManager.Default.NotificationInvoked += Invoked;
            AppNotificationManager.Default.Register("NG Bubbles", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico")));
            registered = true;
        }
        catch (Exception e) { data.Log.Log("notifications-registration-failed", new { type = e.GetType().Name }); }
    }
    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Activated?.Invoke(args.Arguments.TryGetValue("chat", out var guid) ? guid : null);
    public bool Available => registered;
    internal async Task<AppNotification> BuildAsync(IncomingNotification notification, Snapshot? state = null, bool test = false)
    {
        var builder = new AppNotificationBuilder().AddText(notification.Title.Length > 160 ? notification.Title[..160] : notification.Title)
            .AddText(data.Settings.Preview || test ? notification.Body.Length > 250 ? notification.Body[..250] : notification.Body : "New message");
        if (notification.ChatGuid.Length > 0) builder.AddArgument("chat", notification.ChatGuid);
        if (state?.Chats.FirstOrDefault(chat => chat.Guid == notification.ChatGuid) is { } chat)
        {
            try
            {
                var path = await avatars.GetAsync(chat, state);
                builder.SetAppLogoOverride(new Uri(path), chat.IsGroup ? AppNotificationImageCrop.Default : AppNotificationImageCrop.Circle, "Conversation avatar");
            }
            catch (Exception e) { data.Log.Log("notification-avatar-failed", new { type = e.GetType().Name }); }
        }
        return builder.BuildNotification();
    }
    public void Show(IncomingNotification notification, bool test = false)
        => _ = ShowAsync(notification, test: test);
    public async Task ShowAsync(IncomingNotification notification, Snapshot? state = null, bool test = false)
    {
        if (!registered || !data.Settings.Notifications && !test) return;
        try
        {
            var toast = await BuildAsync(notification, state, test);
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception e) { data.Log.Log("notification-failed", new { type = e.GetType().Name }); }
    }
    public void Dispose()
    {
        AppNotificationManager.Default.NotificationInvoked -= Invoked;
        if (registered) AppNotificationManager.Default.Unregister();
    }
}
