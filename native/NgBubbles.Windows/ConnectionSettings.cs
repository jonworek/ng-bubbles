using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NgBubbles.Core;

namespace NgBubbles.Windows;

public sealed class ConnectionSettings : StackPanel, IDisposable
{
    public TextBox Remote { get; } = new() { Header = "Remote server URL (fallback)", PlaceholderText = "https://your-mac.example.com" };
    public TextBox Local { get; } = new() { Header = "Local Mac URL (preferred)", PlaceholderText = "http://macbook.internal:1234" };
    public PasswordBox Password { get; } = new() { Header = "Server password", PlaceholderText = "Leave blank to keep saved password" };
    public TextBlock ConnectionStatus { get; } = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    private readonly Func<string> password;
    private readonly Diagnostics log;
    private readonly string computerId;
    private readonly Func<Preferences> current;
    private readonly Func<ConnectionProfile, Task>? maintain;
    private CancellationTokenSource? watch;
    private CancellationTokenSource? probe;
    private int generation;
    private bool disposed;
    public ConnectionSettings(Preferences settings, Func<string> password, Diagnostics log, Func<Preferences>? current = null, Func<ConnectionProfile, Task>? maintain = null)
    {
        this.password = password; this.log = log; computerId = settings.ServerComputerId; this.current = current ?? (() => settings); this.maintain = maintain;
        Spacing = 14; Remote.Text = settings.Server; Local.Text = settings.LocalServer;
        Children.Add(Local); Children.Add(Remote); Children.Add(ConnectionStatus);
        Children.Add(new TextBlock { Text = "Include the port in the local URL. Local is preferred on your home network or VPN; the remote URL is used when local is unavailable.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        Children.Add(Password);
        Local.TextChanged += (_, _) => _ = ProbeAsync(true);
        Password.PasswordChanged += (_, _) => _ = ProbeAsync(true);
        Loaded += (_, _) => { watch = new(); _ = WatchAsync(watch.Token); };
        Unloaded += (_, _) => { watch?.Cancel(); probe?.Cancel(); generation++; };
    }
    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            await ProbeAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(token)) await ProbeAsync();
        }
        catch (OperationCanceledException) { }
    }
    public async Task ProbeAsync(bool debounce = false)
    {
        if (disposed) return;
        probe?.Cancel();
        var version = ++generation;
        using var cancellation = new CancellationTokenSource(); probe = cancellation;
        var token = cancellation.Token;
        try
        {
            if (Local.Text.Trim().Length == 0)
            { Remote.IsEnabled = true; ConnectionStatus.Text = "Remote URL can be edited manually."; return; }
            if (debounce) await Task.Delay(350, token);
            var secret = Password.Password.Length > 0 ? Password.Password : password();
            if (secret.Length == 0) { Remote.IsEnabled = true; ConnectionStatus.Text = "Enter your password to check the local connection."; return; }
            var local = Model.NormalizeServer(Local.Text.Contains("://") ? Local.Text : "http://" + Local.Text);
            ConnectionStatus.Text = "Checking local connection…";
            using var api = new Api(current().Server, secret, log, localServer: local, computerId: current().ServerComputerId.Length > 0 ? current().ServerComputerId : computerId);
            var progress = new Progress<ConnectionProfile>(profile => { if (!disposed && version == generation) Display(profile); });
            var result = await api.RefreshConnectionAsync(true, progress, token);
            if (!disposed && version == generation)
            { if (maintain != null) await maintain(result); Display(result); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ArgumentException or HttpRequestException)
        {
            if (!disposed && version == generation)
            { Remote.IsEnabled = true; ConnectionStatus.Text = "Local connection unavailable. You can edit the remote URL."; log.Log("settings-local-check-failed", new { type = e.GetType().Name }); }
        }
        finally { if (ReferenceEquals(probe, cancellation)) probe = null; }
    }
    private void Display(ConnectionProfile profile)
    {
        Remote.IsEnabled = !profile.LocalAvailable;
        if (profile.LocalAvailable && profile.RemoteManaged) Remote.Text = profile.RemoteServer;
        else if (profile.LocalAvailable && current().LocalServer == profile.LocalServer && current().Server.Length > 0) Remote.Text = current().Server;
        ConnectionStatus.Text = profile.LocalAvailable ? profile.RemoteManaged ? "Local connection available. The remote address is maintained automatically." :
            "Local connection available. Automatic remote address refresh is unavailable; the saved fallback is unchanged." : "Local connection unavailable. You can edit the remote URL.";
    }
    public void Dispose() { disposed = true; generation++; watch?.Cancel(); probe?.Cancel(); }
}
