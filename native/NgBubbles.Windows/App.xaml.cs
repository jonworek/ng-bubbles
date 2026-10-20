using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NgBubbles.Core;

namespace NgBubbles.Windows;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;
    private AppInstance? instance;
    private Notifications? notifications;
    public static void Trace(string stage)
    {
        if (Environment.GetCommandLineArgs().Contains("--smoke-test") && Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_REPORT") is { } report)
            File.AppendAllText(report + ".trace.txt", DateTimeOffset.UtcNow + " " + stage + Environment.NewLine);
    }
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) CrashReport.Save(exception, "runtime");
        };
        UnhandledException += (_, args) => CrashReport.Fatal(args.Exception, "ui", Main?.Data.Log);
        try { Trace("app-constructor"); InitializeComponent(); Trace("resources-loaded"); }
        catch (Exception exception) { CrashReport.Fatal(exception, "resources"); }
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { await LaunchAsync(); }
        catch (Exception exception) { CrashReport.Fatal(exception, "startup", Main?.Data.Log); }
    }
    private async Task LaunchAsync()
    {
        Trace("launched");
        var smoke = Environment.GetCommandLineArgs().Contains("--smoke-test");
        var data = new LocalData(smoke ? Path.Combine(Path.GetTempPath(), "ng-bubbles-smoke-" + Environment.ProcessId) : null);
        Trace("register-notifications");
        notifications = new(data);
        Trace("notifications-registered");
        Trace("activation-query");
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        instance = AppInstance.FindOrRegisterForKey(smoke ? "ng-bubbles-smoke-" + Environment.ProcessId : "ng-bubbles");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(activation);
            Environment.Exit(0); return;
        }
        Trace("create-window");
        Main = new MainWindow(data, notifications, smoke);
        notifications.Activated += guid => Main.DispatcherQueue.TryEnqueue(() => Main.ActivateChat(guid));
        instance.Activated += (_, e) => Main.DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Kind == ExtendedActivationKind.AppNotification && e.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notification)
                Main.ActivateChat(notification.Arguments.TryGetValue("chat", out var redirectedGuid) ? redirectedGuid : null);
            else Main.ActivateChat(null);
        });
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs launch)
            Main.ActivateChat(launch.Arguments.TryGetValue("chat", out var launchGuid) ? launchGuid : null);
        else if (!Environment.GetCommandLineArgs().Contains("--hidden")) Main.Activate();
        Trace("start-client");
        await Main.StartAsync();
        Trace("client-started");
        if (smoke)
        {
            try { await Main.SmokeTestAsync(); }
            catch (Exception e)
            {
                if (Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_REPORT") is { } report)
                {
                    await File.WriteAllTextAsync(report, data.Log.Report());
                    await File.WriteAllTextAsync(report + ".error.txt", e.ToString());
                }
                Environment.Exit(1);
            }
        }
    }
}
