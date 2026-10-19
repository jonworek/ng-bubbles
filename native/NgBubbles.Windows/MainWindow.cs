using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Win32;
using NgBubbles.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using global::Windows.Storage.Pickers;
using global::Windows.System;
using global::Windows.ApplicationModel.DataTransfer;

namespace NgBubbles.Windows;

public sealed class MainWindow : Window
{
    public LocalData Data { get; }
    public MessagingClient Client { get; }
    public MediaCache Media { get; }
    private readonly Notifications notifications;
    private readonly bool smoke;
    private readonly WindowStateController windowState;
    private readonly ReleaseUpdater updater = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly SemaphoreSlim updateChecks = new(1, 1);
    private readonly InfoBar updateBar = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Informational };
    private readonly Button installUpdate = new() { Content = "Download and install" };
    private ReleaseUpdate? availableUpdate;
    private string updateMessage = "";
    private bool downloadingUpdate;
    private readonly Grid root = new() { ColumnSpacing = 0 };
    private readonly ObservableCollection<ChatItem> chats = [];
    private readonly ObservableCollection<MessageItem> messages = [];
    private readonly ListView chatList = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView messageList = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox search = new() { PlaceholderText = "Search conversations", Margin = new(12, 8, 12, 12) };
    private readonly TextBox composer = new() { PlaceholderText = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 160, MaxLength = 20000, IsSpellCheckEnabled = true, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock status = new() { Text = "Offline", Opacity = .65, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock heading = new() { Text = "Your conversations, here.", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock subheading = new() { Text = "Select a conversation to begin", Opacity = .65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Avatar avatar = new();
    private readonly InfoBar error = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning };
    private readonly Button send = new() { Content = "Send", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button older = new() { Content = "Load older messages", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer paint = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Dictionary<string, ChatItem> rows = [];
    private readonly Dictionary<string, MessageItem> messageRows = [];
    private TrayIcon? tray;
    private Snapshot state;
    private string? draftGuid;
    private bool changingDraft;
    private bool quitting;
    private bool sending;
    private bool dialogOpen;
    private bool openingFromNotification;
    private int dirty;
    private bool scrollBottom;
    public MainWindow(LocalData data, Notifications notifications, bool smoke)
    {
        Data = data; this.notifications = notifications; this.smoke = smoke;
        Client = new(data.Store, data.Log, data.PhotoAsync); Media = new(data);
        state = Client.Snapshot();
        Title = $"NG Bubbles v{AppInfo.Version}";
        windowState = new(this, data);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        SystemBackdrop = new MicaBackdrop();
        Build();
        Client.Changed += () => Interlocked.Exchange(ref dirty, 1);
        Client.Notification += notification => DispatcherQueue.TryEnqueue(() => notifications.Show(notification));
        paint.Tick += (_, _) => { if (Interlocked.Exchange(ref dirty, 0) == 1) Render(); };
        paint.Start();
        Activated += async (_, args) =>
        {
            Client.Focused = args.WindowActivationState != WindowActivationState.Deactivated && AppWindow.IsVisible;
            if (Client.Focused && !openingFromNotification && Client.Active is { } guid) await RunAsync(() => Task.Run(() => Client.ReadAsync(guid)));
        };
        AppWindow.Closing += (_, args) => { windowState.Capture(); _ = windowState.SaveAsync(); if (!quitting && tray != null) { args.Cancel = true; Client.Focused = false; AppWindow.Hide(); } };
        Closed += async (_, _) => { paint.Stop(); windowState.Stop(); windowLifetime.Cancel(); updater.Dispose(); tray?.Dispose(); notifications.Dispose(); await windowState.SaveAsync(); await Client.SaveAsync(); Client.Dispose(); Application.Current.Exit(); };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try { tray = new(hwnd, () => ActivateChat(null), Client.RequestSync, () => _ = QuitAsync()); }
        catch (Exception e) { data.Log.Log("tray-failed", new { type = e.GetType().Name }); }
        Render();
    }
    public async Task StartAsync()
    {
        if (smoke) return;
        ConfigureStartup();
        _ = WatchUpdatesAsync();
        var migrationNotice = Data.Settings.MigrationNotice;
        var password = Data.Password();
        if (Data.Settings.Server.Length == 0 || password.Length == 0) { ActivateChat(null); await SettingsAsync(); }
        else await RunAsync(() => Task.Run(() => Client.ConnectAsync(Data.Settings, password)));
        if (migrationNotice.Length > 0) ShowError(migrationNotice);
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action(); return button;
    }
    private void Build()
    {
        root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        root.ColumnDefinitions.Add(new() { Width = new(320) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var sidebar = new Grid { Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) sidebar.RowDefinitions.Add(new() { Height = height });
        var brand = new Grid { Margin = new(16, 20, 12, 16), ColumnSpacing = 8 };
        brand.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(new TextBlock { Text = "NG Bubbles", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = $"Not Green · v{AppInfo.Version}", FontSize = 12, Opacity = .65 }); brand.Children.Add(title);
        var add = Button("+", () => _ = NewChatAsync()); ToolTipService.SetToolTip(add, "New conversation"); Grid.SetColumn(add, 1); brand.Children.Add(add);
        sidebar.Children.Add(brand); Grid.SetRow(search, 1); sidebar.Children.Add(search);
        chatList.ItemsSource = chats; chatList.ItemTemplate = Template("ChatRow");
        chatList.ItemContainerStyle = ContainerStyle();
        chatList.SelectionChanged += async (_, _) => { if (chatList.SelectedItem is ChatItem item && item.Guid != Client.Active) await SelectAsync(item.Guid); };
        chatList.ItemClick += async (_, args) => { if (args.ClickedItem is ChatItem item) await SelectAsync(item.Guid); };
        Grid.SetRow(chatList, 2); sidebar.Children.Add(chatList);
        var footer = new Grid { Margin = new(12), ColumnSpacing = 6 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(status); var settings = Button("Settings", () => _ = SettingsAsync()); Grid.SetColumn(settings, 1); footer.Children.Add(settings);
        Grid.SetRow(footer, 3); sidebar.Children.Add(footer); root.Children.Add(sidebar);
        var content = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) content.RowDefinitions.Add(new() { Height = height });
        var header = new Grid { Padding = new(20, 16, 20, 16), ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(avatar); var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; name.Children.Add(heading); name.Children.Add(subheading); Grid.SetColumn(name, 1); header.Children.Add(name);
        var sync = Button("Sync", () => { Client.RequestSync(); _ = RunAsync(() => Task.Run(Client.RefreshPhotosAsync)); }); Grid.SetColumn(sync, 2); header.Children.Add(sync); content.Children.Add(header);
        var notices = new StackPanel(); notices.Children.Add(error); notices.Children.Add(updateBar);
        updateBar.ActionButton = installUpdate;
        installUpdate.Click += async (_, _) => await UpdatesAsync(false);
        Grid.SetRow(notices, 1); content.Children.Add(notices);
        older.Click += async (_, _) => { if (state.Active is { } guid && messages.Count > 0) await RunAsync(() => { var before = messages[0].Message.DateCreated; return Task.Run(() => Client.OpenAsync(guid, before)); }); };
        Grid.SetRow(older, 2); content.Children.Add(older);
        messageList.ItemsSource = messages; messageList.ItemTemplate = Template("MessageRow"); messageList.ItemContainerStyle = ContainerStyle();
        messageList.Loaded += (_, _) => Scroll(); Grid.SetRow(messageList, 3); content.Children.Add(messageList);
        var entry = new Grid { Margin = new(20, 12, 20, 16), ColumnSpacing = 10, RowSpacing = 6 };
        entry.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); entry.RowDefinitions.Add(new() { Height = GridLength.Auto }); entry.RowDefinitions.Add(new() { Height = GridLength.Auto });
        entry.Children.Add(composer); send.Click += async (_, _) => await SendAsync(); Grid.SetColumn(send, 1); entry.Children.Add(send);
        var hint = new TextBlock { Text = "Ctrl+Enter to send · Enter for a new line · Drafts saved locally", FontSize = 11, Opacity = .6 }; Grid.SetRow(hint, 1); Grid.SetColumnSpan(hint, 2); entry.Children.Add(hint); Grid.SetRow(entry, 4); content.Children.Add(entry);
        Grid.SetColumn(content, 1); root.Children.Add(content); Content = root;
        search.TextChanged += (_, _) => Render();
        composer.TextChanged += (_, _) => { if (!changingDraft && draftGuid is { } guid) Client.Draft(guid, composer.Text); };
        var sendShortcut = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        sendShortcut.Invoked += async (_, args) => { args.Handled = true; await SendAsync(); };
        composer.KeyboardAccelerators.Add(sendShortcut);
        var diagnostics = new KeyboardAccelerator { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        diagnostics.Invoked += (sender, args) => { args.Handled = true; _ = DiagnosticsAsync(); }; root.KeyboardAccelerators.Add(diagnostics);
        var quit = new KeyboardAccelerator { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control };
        quit.Invoked += (sender, args) => { args.Handled = true; _ = QuitAsync(); }; root.KeyboardAccelerators.Add(quit);
    }
    private static DataTemplate Template(string control) => (DataTemplate)Application.Current.Resources[control + "Template"];
    private static Style ContainerStyle()
    {
        var style = new Style(typeof(ListViewItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0))); return style;
    }
    private void Render()
    {
        var previous = state;
        state = Client.Snapshot();
        var namesChanged = previous.Contacts.Count != state.Contacts.Count || previous.Contacts.Any(p => state.Contacts.GetValueOrDefault(p.Key) != p.Value);
        Title = (state.Unread.Count > 0 ? $"({state.Unread.Count}) " : "") + $"NG Bubbles v{AppInfo.Version}";
        tray?.Update(state.Unread.Count > 0, state.Unread.Count);
        status.Text = state.Status switch { "connected" => "Connected to your Mac", "connecting" => "Connecting…", _ => "Offline · reconnecting" };
        if (state.Error.Length > 0) ShowError(state.Error);
        var wanted = state.Chats.Where(c => Model.Title(c, state.Contacts).Contains(search.Text, StringComparison.OrdinalIgnoreCase) || c.Participants.Any(p => p.Address.Contains(search.Text, StringComparison.OrdinalIgnoreCase))).ToList();
        var wantedIds = wanted.Select(c => c.Guid).ToHashSet();
        for (var i = chats.Count - 1; i >= 0; i--) if (!wantedIds.Contains(chats[i].Guid)) chats.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!rows.TryGetValue(wanted[i].Guid, out var row)) { row = new(wanted[i], state); rows[row.Guid] = row; } else row.Update(wanted[i], state);
            var old = chats.IndexOf(row); if (old < 0) chats.Insert(i, row); else if (old != i) chats.Move(old, i);
        }
        if (state.Active != draftGuid)
        {
            draftGuid = state.Active; changingDraft = true; composer.Text = draftGuid == null ? "" : Client.Draft(draftGuid); changingDraft = false;
            messages.Clear(); messageRows.Clear(); scrollBottom = true;
        }
        var current = state.Chats.FirstOrDefault(c => c.Guid == state.Active);
        chatList.SelectedItem = chats.FirstOrDefault(c => c.Guid == state.Active);
        heading.Text = current == null ? "Your conversations, here." : Model.Title(current, state.Contacts);
        subheading.Text = current == null ? "A client for your BlueBubbles server" : string.Join(", ", current.Participants.Select(p => p.Address));
        if (current != null) avatar.Set(current, state);
        composer.IsEnabled = current != null; send.IsEnabled = current != null && !sending;
        older.Visibility = current != null && state.Messages.Count >= 100 ? Visibility.Visible : Visibility.Collapsed;
        var ids = state.Messages.Select(m => m.Guid).ToHashSet();
        for (var i = messages.Count - 1; i >= 0; i--) if (!ids.Contains(messages[i].Message.Guid)) messages.RemoveAt(i);
        var lastBefore = messages.LastOrDefault()?.Message.Guid;
        for (var i = 0; i < state.Messages.Count; i++)
        {
            var value = state.Messages[i];
            if (!messageRows.TryGetValue(value.Guid, out var row) || namesChanged || Signature(row.Message) != Signature(value)) { row = new(value, state); messageRows[value.Guid] = row; }
            if (i >= messages.Count) messages.Add(row);
            else if (messages[i].Message.Guid != value.Guid) messages.Insert(i, row);
            else if (!ReferenceEquals(messages[i], row)) messages[i] = row;
        }
        if (scrollBottom || lastBefore != messages.LastOrDefault()?.Message.Guid) { Scroll(); scrollBottom = false; }
    }
    private static string Signature(Message message) => message.Guid + "|" + message.Text + "|" + message.DateDelivered + "|" + message.DateRead + "|" + message.IsRead + "|" + string.Join(',', message.Attachments.Select(a => a.Guid + a.TransferName + a.MimeType + a.TotalBytes));
    public bool FollowingLatest => Visuals(messageList).OfType<ScrollViewer>().FirstOrDefault() is { } viewer && viewer.ScrollableHeight - viewer.VerticalOffset < 48;
    public void MediaLayoutChanged(bool follow)
    {
        if (!follow) return;
        messageList.UpdateLayout();
        Scroll();
    }
    private void Scroll() { if (messages.Count > 0) DispatcherQueue.TryEnqueue(() => { if (messages.Count > 0) messageList.ScrollIntoView(messages[^1]); }); }
    private async Task SelectAsync(string guid)
    {
        if (draftGuid is { } previous) Client.Draft(previous, composer.Text);
        scrollBottom = true;
        await RunAsync(() => Task.Run(() => Client.OpenAsync(guid)));
        Render(); composer.Focus(FocusState.Programmatic);
    }
    public void ActivateChat(string? guid)
    {
        if (guid != null)
        {
            openingFromNotification = true;
            if (draftGuid is { } previous) Client.Draft(previous, composer.Text);
            Client.Select(guid);
        }
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Restore();
        Activate(); SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (guid != null)
        {
            search.Text = "";
            _ = SelectAndRevealAsync(guid);
        }
    }
    private async Task SelectAndRevealAsync(string guid)
    {
        try
        {
            await SelectAsync(guid);
            var row = chats.FirstOrDefault(c => c.Guid == guid);
            if (row != null) { chatList.SelectedItem = row; chatList.ScrollIntoView(row); }
        }
        finally { openingFromNotification = false; }
    }
    private async Task SendAsync()
    {
        if (sending || draftGuid == null || string.IsNullOrWhiteSpace(composer.Text)) return;
        var guid = draftGuid; var text = composer.Text; sending = true; send.IsEnabled = false;
        try
        {
            await Task.Run(() => Client.SendAsync(guid, text));
            if (Client.Draft(guid) == text) Client.Draft(guid, "");
            if (draftGuid == guid && composer.Text == text) composer.Text = "";
        }
        catch (Exception e) { Data.Log.Log("send-failed", new { type = e.GetType().Name }); ShowError("Send was not confirmed. Your draft is kept. Check delivery before sending again."); }
        finally { sending = false; send.IsEnabled = draftGuid != null; composer.Focus(FocusState.Programmatic); }
    }
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Data.Log.Log("operation-failed", new { type = e.GetType().Name }); ShowError(e is IOException or ArgumentException ? e.Message : "The operation failed. Open Diagnostics for technical details."); }
    }
    private void ShowError(string text) { error.Message = text; error.IsOpen = true; }
    public async Task SaveAttachmentAsync(Attachment attachment)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileName(attachment.TransferName) };
        var ext = Path.GetExtension(attachment.TransferName); if (ext.Length == 0) ext = ".bin";
        picker.FileTypeChoices.Add("Attachment", [ext]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await Client.Connection.DownloadAsync(attachment.Guid, file.Path, timeout.Token);
    }
    private async Task SettingsAsync()
    {
        if (dialogOpen) return; dialogOpen = true;
        try
        {
            if (root.XamlRoot == null)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler? handler = null;
                handler = (_, _) => { root.Loaded -= handler; loaded.TrySetResult(); };
                root.Loaded += handler;
                Activate();
                if (root.XamlRoot != null) { root.Loaded -= handler; loaded.TrySetResult(); }
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            var current = Data.Settings;
            var server = new TextBox { Header = "BlueBubbles server URL", Text = current.Server, PlaceholderText = "https://your-mac.example.com" };
            var password = new PasswordBox { Header = "Server password", PlaceholderText = "Leave blank to keep saved password" };
            var method = new ComboBox { Header = "Sending method", ItemsSource = new[] { "Standard API", "Private API" }, SelectedIndex = current.SendMethod == "private-api" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var enabled = new ToggleSwitch { Header = "Notifications", IsOn = current.Notifications };
            var preview = new ToggleSwitch { Header = "Message previews in notifications", IsOn = current.Preview };
            var startup = new ToggleSwitch { Header = "Start with Windows", IsOn = current.LaunchAtLogin };
            var panel = new StackPanel { Spacing = 14, MinWidth = 400 }; panel.Children.Add(server); panel.Children.Add(password); panel.Children.Add(method); panel.Children.Add(enabled); panel.Children.Add(preview); panel.Children.Add(startup);
            panel.Children.Add(new TextBlock { Text = "Messages are automatically marked read on your Mac when Private API is available.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = .7 });
            var test = Button("Test notification", () => { if (!notifications.Available) ShowError("Windows notification registration failed. See Diagnostics."); else notifications.Show(new("", "NG Bubbles", "Notifications are working."), true); }); panel.Children.Add(test);
            var openUpdates = false;
            var updatesButton = new Button { Content = "Check for updates" }; panel.Children.Add(updatesButton);
            var dialog = new ContentDialog { Title = "NG Bubbles settings", Content = new ScrollViewer { Content = panel }, PrimaryButtonText = "Save and connect", SecondaryButtonText = "Diagnostics", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot };
            updatesButton.Click += (_, _) => { openUpdates = true; dialog.Hide(); };
            var result = await dialog.ShowAsync();
            if (openUpdates) { dialogOpen = false; await UpdatesAsync(true); return; }
            if (result == ContentDialogResult.Secondary) { dialogOpen = false; await DiagnosticsAsync(); return; }
            if (result != ContentDialogResult.Primary) return;
            await RunAsync(async () =>
            {
                var origin = Model.NormalizeServer(server.Text);
                var secret = password.Password.Length > 0 ? password.Password : Data.Password();
                if (secret.Length == 0) throw new IOException("Enter your server password.");
                var preferences = new Preferences { Server = origin, SendMethod = method.SelectedIndex == 1 ? "private-api" : "apple-script", Notifications = enabled.IsOn, Preview = preview.IsOn, LaunchAtLogin = startup.IsOn };
                using var check = new Api(origin, secret, Data.Log); await check.RequestAsync("/ping");
                if (origin != current.Server && current.Server.Length > 0) throw new IOException("Changing servers requires a separate Windows data folder to avoid mixing conversations. Keep the existing server for this update.");
                await Data.SaveSettingsAsync(preferences, secret);
                ConfigureStartup();
                await Task.Run(() => Client.ConnectAsync(preferences, secret));
            });
        }
        finally { dialogOpen = false; }
    }
    private void ConfigureStartup()
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Data.Settings.LaunchAtLogin) run.SetValue("NG Bubbles", "\"" + Environment.ProcessPath + "\" --hidden");
            else run.DeleteValue("NG Bubbles", false);
            foreach (var name in new[] { "Not Green Bubbles", "BlueBubbles Desktop", "not-green-bubbles-windows", "bluebubbles-desktop" })
            {
                if (run.GetValue(name) is not string command) continue;
                if (command.Contains("Not Green Bubbles.exe", StringComparison.OrdinalIgnoreCase) || command.Contains("BlueBubbles Desktop.exe", StringComparison.OrdinalIgnoreCase)) run.DeleteValue(name, false);
            }
        }
        catch (Exception e) { Data.Log.Log("startup-registration-failed", new { type = e.GetType().Name }); ShowError("Windows startup registration could not be updated. Check Diagnostics."); }
    }
    private async Task WatchUpdatesAsync()
    {
        try
        {
            await CheckUpdatesAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (await timer.WaitForNextTickAsync(windowLifetime.Token)) await CheckUpdatesAsync();
        }
        catch (OperationCanceledException) { }
    }
    private async Task CheckUpdatesAsync()
    {
        try { await updateChecks.WaitAsync(windowLifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            availableUpdate = await updater.CheckAsync(AppInfo.Version, windowLifetime.Token);
            updateMessage = availableUpdate == null ? $"You're up to date. NG Bubbles v{AppInfo.Version}." : $"NG Bubbles v{availableUpdate.Version} is available.";
            updateBar.Title = updateMessage;
            updateBar.Message = "Download the update and start the installer when you're ready.";
            updateBar.IsOpen = availableUpdate != null;
            Data.Log.Log("update-checked", new { available = availableUpdate != null, version = availableUpdate?.Version });
        }
        catch (Exception e)
        {
            if (windowLifetime.IsCancellationRequested) return;
            updateMessage = "Couldn't check for updates. Please try again later.";
            Data.Log.Log("update-check-failed", new { type = e.GetType().Name });
        }
        finally { updateChecks.Release(); }
    }
    private ContentDialog CreateUpdatesDialog(ReleaseUpdate? release) => new()
    {
        Title = "NG Bubbles updates", Content = new TextBlock { Text = updateMessage + (release == null ? "" : "\n\nYour drafts, settings and window size will be saved. The app will close and the installer will open."), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
        PrimaryButtonText = release == null ? "" : "Download and install", CloseButtonText = release == null ? "Close" : "Later", XamlRoot = root.XamlRoot
    };
    private async Task UpdatesAsync(bool check)
    {
        if (dialogOpen || downloadingUpdate) return;
        dialogOpen = true;
        ReleaseUpdate? release;
        try
        {
            if (check)
            {
                updateBar.Title = "Checking for updates…"; updateBar.Message = ""; updateBar.IsOpen = true; installUpdate.IsEnabled = false;
                await CheckUpdatesAsync();
                installUpdate.IsEnabled = true; updateBar.IsOpen = availableUpdate != null;
            }
            if (windowLifetime.IsCancellationRequested) return;
            release = availableUpdate;
            var dialog = CreateUpdatesDialog(release);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || release == null) return;
        }
        finally { dialogOpen = false; }
        downloadingUpdate = true; installUpdate.IsEnabled = false;
        try
        {
            updateBar.IsOpen = true; updateBar.Title = "Downloading update…";
            var progress = new Progress<int>(percent => updateBar.Message = $"{percent}% downloaded");
            var installer = await updater.DownloadAsync(release, Path.Combine(Data.Store.DirectoryPath, "updates"), progress, windowLifetime.Token);
            await Client.SaveAsync(); windowState.Capture(); await windowState.SaveAsync();
            windowLifetime.Token.ThrowIfCancellationRequested();
            if (Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }) == null) throw new IOException("The installer could not be started.");
            await QuitAsync();
        }
        catch (Exception e)
        {
            if (!windowLifetime.IsCancellationRequested)
            {
                Data.Log.Log("update-install-failed", new { type = e.GetType().Name });
                updateBar.Title = "Update could not be installed";
                updateBar.Message = "Your app and data are unchanged. Try again, or download the installer from github.com/jonworek/ng-bubbles/releases.";
                updateBar.IsOpen = true;
            }
        }
        finally { downloadingUpdate = false; installUpdate.IsEnabled = true; }
    }
    private async Task DiagnosticsAsync()
    {
        if (dialogOpen) return; dialogOpen = true;
        try
        {
            var text = new TextBox { Text = Data.Log.Report(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 550, MaxHeight = 440, FontFamily = new("Consolas") };
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(new TextBlock { Text = "Technical events only. No passwords, message bodies, contact names or server URLs.", TextWrapping = TextWrapping.Wrap, MaxWidth = 550 }); panel.Children.Add(text);
            var dialog = new ContentDialog { Title = $"Diagnostics · NG Bubbles v{AppInfo.Version}", Content = panel, PrimaryButtonText = "Copy report", SecondaryButtonText = "Licenses", CloseButtonText = "Close", XamlRoot = root.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) { var package = new DataPackage(); package.SetText(text.Text); Clipboard.SetContent(package); }
            if (result == ContentDialogResult.Secondary) Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NATIVE_NOTICES.md")) { UseShellExecute = true });
        }
        finally { dialogOpen = false; }
    }
    private async Task NewChatAsync()
    {
        if (dialogOpen) return; dialogOpen = true;
        try
        {
            var recipients = new TextBox { Header = "Recipients", PlaceholderText = "Phone numbers or emails, separated by commas" };
            var text = new TextBox { Header = "First message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 20000 };
            var service = new ComboBox { Header = "Service", ItemsSource = new[] { "iMessage", "SMS" }, SelectedIndex = 0 };
            var panel = new StackPanel { Spacing = 12, MinWidth = 420 }; panel.Children.Add(recipients); panel.Children.Add(service); panel.Children.Add(text);
            var dialog = new ContentDialog { Title = "New conversation", Content = panel, PrimaryButtonText = "Send", CloseButtonText = "Cancel", XamlRoot = root.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(async () => { var addresses = recipients.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); var body = text.Text; var chosen = (string)service.SelectedItem; var guid = await Task.Run(() => Client.CreateChatAsync(addresses, body, chosen)); await SelectAsync(guid); });
        }
        finally { dialogOpen = false; }
    }
    private async Task QuitAsync() { windowState.Capture(); await windowState.SaveAsync(); await RunAsync(Client.SaveAsync); quitting = true; Close(); }
    public async Task SmokeTestAsync()
    {
        AppWindow.Resize(new(Math.Max(640, AppWindow.Size.Width - 80), Math.Max(480, AppWindow.Size.Height - 60)));
        await Task.Delay(750);
        var savedWindow = Data.Store.Load("window.json", () => new WindowSettings(0, 0));
        if (Math.Abs(savedWindow.Width * windowState.Scale() - AppWindow.Size.Width) > 2 || savedWindow.Maximized)
            throw new InvalidOperationException("Resized window dimensions were not saved.");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize(); await Task.Delay(750);
            var maximized = Data.Store.Load("window.json", () => new WindowSettings());
            if (!maximized.Maximized || Math.Abs(maximized.Width - savedWindow.Width) > 2)
                throw new InvalidOperationException("Maximizing overwrote the normal window size.");
            presenter.Restore(); await Task.Delay(750);
        }
        Data.Log.Log("window-persistence-passed", new { resized = true, maximized = true });
        updateMessage = "Synthetic update available";
        var updateDialog = CreateUpdatesDialog(new ReleaseUpdate("999.0.0", new Uri("https://github.com/jonworek/ng-bubbles/releases"), new string('0', 64), 1));
        var updateOpened = false;
        updateDialog.Opened += (_, _) => updateOpened = true;
        var showingUpdate = updateDialog.ShowAsync();
        await Task.Delay(250);
        if (!updateOpened || updateDialog.PrimaryButtonText != "Download and install") throw new InvalidOperationException("The update dialog did not open correctly.");
        updateDialog.Hide(); await showingUpdate;
        updateMessage = "";
        Data.Log.Log("update-dialog-passed", new { confirmation = true });
        App.Trace("smoke-connect");
        var config = Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_CONFIG") ?? throw new IOException("Synthetic fixture configuration missing.");
        var fixture = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(config))!;
        await Task.Run(() => Client.ConnectAsync(new Preferences { Server = fixture["server"] }, "test-password"));
        App.Trace("smoke-open-chat");
        await SelectAsync(fixture["guid"]);
        App.Trace("smoke-chat-opened");
        await Task.Delay(2500);
        Render();
        App.Trace("smoke-validate");
        if (chats.Count < 3 || messages.Count < 3 || !composer.IsEnabled || !Title.EndsWith($"NG Bubbles v{AppInfo.Version}")) throw new InvalidOperationException("Native UI conversation test failed.");
        composer.Text = "Dictated café — 💬";
        await Task.Delay(150);
        if (Client.Draft(fixture["guid"]) != composer.Text) throw new InvalidOperationException("Native text entry did not preserve Unicode draft.");
        var assetRoot = Environment.GetEnvironmentVariable("NG_BUBBLES_FIXTURES");
        if (assetRoot != null)
        {
            foreach (var name in new[] { "contact.jpg", "contact.tiff" })
            {
                var image = await Data.NormalizeImageAsync(await File.ReadAllBytesAsync(Path.Combine(assetRoot, name)), 96);
                if (image.Length == 0) throw new InvalidOperationException("Windows image decoder failed: " + name);
            }
        }
        if (Client.Snapshot().ContactPhotos.Count == 0) throw new InvalidOperationException("Native contact photos were not loaded.");
        Client.Focused = false;
        Client.Accept(new Message { Guid = "synthetic-unseen-activation", ChatGuid = fixture["guid"], Text = "I'll bring the snacks.", DateCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, false);
        ActivateChat("synthetic-group");
        await Task.Delay(500);
        if (Client.Active != "synthetic-group" || !Client.Snapshot().Unread.Contains(fixture["guid"]) || chatList.SelectedItem is not ChatItem selected || selected.Guid != "synthetic-group") throw new InvalidOperationException("Notification selection did not preserve the other unread conversation.");
        Client.Focused = true;
        await SelectAsync(fixture["guid"]);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pictureReady = Visuals(root).OfType<PersonPicture>().Any(p => p.ProfilePicture is Microsoft.UI.Xaml.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth > 0);
            var inlineReady = Visuals(messageList).OfType<Image>().Any(p => p.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth > 0);
            if (pictureReady && inlineReady) break;
            if (attempt == 99) throw new InvalidOperationException("Contact portrait or inline image did not become visible.");
            await Task.Delay(50);
        }
        await Task.Delay(200);
        var stablePortraits = Visuals(root).OfType<PersonPicture>().Select(picture => (picture, source: picture.ProfilePicture)).ToArray();
        if (stablePortraits.Length < 3 || !stablePortraits.Any(p => p.source != null)) throw new InvalidOperationException("Avatar stability test requires visible contact and group portraits.");
        for (var refresh = 0; refresh < 20; refresh++)
        {
            Render();
            root.UpdateLayout();
            var currentPortraits = Visuals(root).OfType<PersonPicture>().ToHashSet();
            if (stablePortraits.Any(p => !currentPortraits.Contains(p.picture) || !ReferenceEquals(p.picture.ProfilePicture, p.source)))
                throw new InvalidOperationException("Conversation refresh replaced an unchanged avatar or its image.");
            await Task.Delay(25);
        }
        Data.Log.Log("avatar-stability-passed", new { refreshes = 20, portraits = stablePortraits.Length });
        Data.Log.Log("ui-smoke-passed", new { conversations = chats.Count, messages = messages.Count, nativeText = true, notifications = notifications.Available, tray = tray != null });
        var target = Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_REPORT");
        if (target != null)
        {
            await File.WriteAllTextAsync(target, Data.Log.Report());
            App.Trace("smoke-capture");
            var capture = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await capture.RenderAsync(root);
            var pixels = await capture.GetPixelsAsync();
            using var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(pixels);
            var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)capture.PixelWidth, (uint)capture.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            using var result = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await result.LoadAsync((uint)stream.Size); var png = new byte[stream.Size]; result.ReadBytes(png);
            await File.WriteAllBytesAsync(Path.ChangeExtension(target, ".png"), png);
            await File.WriteAllTextAsync(target, Data.Log.Report());
        }
        App.Trace("smoke-quit");
        await QuitAsync();
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
