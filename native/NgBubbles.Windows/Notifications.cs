using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NgBubbles.Core;

namespace NgBubbles.Windows;

public sealed class Notifications : IDisposable
{
    private readonly LocalData data;
    public event Action<string?>? Activated;
    private bool registered;
    public Notifications(LocalData data)
    {
        this.data = data;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += Invoked;
            AppNotificationManager.Default.Register("NG Bubbles", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico")));
            registered = true;
        }
        catch (Exception e) { data.Log.Log("notifications-registration-failed", new { type = e.GetType().Name }); }
    }
    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Activated?.Invoke(args.Arguments.GetValueOrDefault("chat"));
    public bool Available => registered;
    public void Show(IncomingNotification notification, bool test = false)
    {
        if (!registered || !data.Settings.Notifications && !test) return;
        try
        {
            var builder = new AppNotificationBuilder().AddText(notification.Title.Length > 160 ? notification.Title[..160] : notification.Title)
                .AddText(data.Settings.Preview || test ? notification.Body.Length > 250 ? notification.Body[..250] : notification.Body : "New message");
            if (notification.ChatGuid.Length > 0) builder.AddArgument("chat", notification.ChatGuid);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e) { data.Log.Log("notification-failed", new { type = e.GetType().Name }); }
    }
    public void Dispose()
    {
        AppNotificationManager.Default.NotificationInvoked -= Invoked;
        if (registered) AppNotificationManager.Default.Unregister();
    }
}
