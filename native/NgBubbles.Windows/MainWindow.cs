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
    private readonly HomeDashboard dashboard = new();
    private readonly Button home = new() { Content = new SymbolIcon(Symbol.Home) };
    private readonly Grid entry = new() { Margin = new(20, 12, 20, 16), ColumnSpacing = 10, RowSpacing = 6 };
    private readonly TextBox search = new() { PlaceholderText = "Search conversations" };
    private readonly Button filterButton = new() { VerticalAlignment = VerticalAlignment.Stretch, Padding = new(10, 0, 10, 0) };
    private readonly ToggleMenuFlyoutItem unreadFilter = new() { Text = "Unread only" };
    private readonly ToggleMenuFlyoutItem groupFilter = new() { Text = "Groups only" };
    private readonly MenuFlyoutItem clearFilters = new() { Text = "Clear filters" };
    private readonly TextBlock filterCount = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock emptyChats = new() { Margin = new(20), TextWrapping = TextWrapping.Wrap, Opacity = .65, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBox composer = new() { PlaceholderText = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 160, MaxLength = 20000, IsSpellCheckEnabled = true, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock status = new() { Text = "Offline", Opacity = .65, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock heading = new() { Text = "Your conversations, here.", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock subheading = new() { Text = "Select a conversation to begin", Opacity = .65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Avatar avatar = new();
    private readonly Button retryConnection = new() { Content = "Retry", Name = "RetryConnection" };
    private readonly Button restartServer = new() { Content = "Restart server", Name = "RestartServer" };
    private readonly StackPanel recoveryActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private bool recovering;
    private bool recoveryConnecting;
    private readonly InfoBar error = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning };
    private readonly Button send = new() { Content = "Send", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ProgressRing historyProgress = new() { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 8, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly List<AttachmentViewer> attachmentWindows = [];
    private readonly HashSet<string> loadingHistory = [];
    private readonly Dictionary<string, long> historyRetryAt = [];
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
    private bool updatingConversationList;
    private int dirty;
    private bool scrollBottom;
    private int scrollVersion;
    private bool scrolling;
    private bool followLatest = true;
    private bool userScrolling;
    private ScrollViewer? messageViewer;
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
        Client.Notification += notification => DispatcherQueue.TryEnqueue(() => _ = notifications.ShowAsync(notification, Client.Snapshot()));
        paint.Tick += (_, _) => { if (Interlocked.Exchange(ref dirty, 0) == 1) Render(); MaintainScroll(); };
        paint.Start();
        Activated += async (_, args) =>
        {
            Client.Focused = args.WindowActivationState != WindowActivationState.Deactivated && AppWindow.IsVisible;
            if (Client.Focused && !openingFromNotification && Client.Active is { } guid) await RunAsync(() => Task.Run(() => Client.ReadAsync(guid, force: true)));
        };
        AppWindow.Closing += (sender, args) => { windowState.Capture(); _ = windowState.SaveAsync(); if (!quitting && tray != null) { args.Cancel = true; Client.Focused = false; AppWindow.Hide(); } };
        Closed += async (_, _) => { foreach (var viewer in attachmentWindows.ToArray()) viewer.Close(); paint.Stop(); windowState.Stop(); windowLifetime.Cancel(); updater.Dispose(); tray?.Dispose(); notifications.Dispose(); await windowState.SaveAsync(); await Client.SaveAsync(); Client.Dispose(); Application.Current.Exit(); };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try { tray = new(hwnd, () => ActivateChat(null), Client.RequestSync, () => _ = QuitAsync(), () => { ActivateChat(null); _ = UpdatesAsync(true); }); }
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
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) sidebar.RowDefinitions.Add(new() { Height = height });
        var brand = new Grid { Margin = new(16, 20, 12, 16), ColumnSpacing = 8 };
        brand.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(new TextBlock { Text = "NG Bubbles", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = $"Not Green · v{AppInfo.Version}", FontSize = 12, Opacity = .65 }); brand.Children.Add(title);
        home.Click += (_, _) => ShowHome(); ToolTipService.SetToolTip(home, "Home");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(home, "Home");
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; navigation.Children.Add(home);
        var add = Button("+", () => _ = NewChatAsync()); ToolTipService.SetToolTip(add, "New conversation"); navigation.Children.Add(add); Grid.SetColumn(navigation, 1); brand.Children.Add(navigation);
        sidebar.Children.Add(brand);
        var searchRow = new Grid { Margin = new(12, 8, 12, 12), ColumnSpacing = 6 };
        searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchRow.Children.Add(search);
        var filterIcon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        filterIcon.Children.Add(new SymbolIcon(Symbol.Filter) { Width = 16, Height = 16 }); filterIcon.Children.Add(filterCount); filterButton.Content = filterIcon;
        var filters = new MenuFlyout(); filters.Items.Add(unreadFilter); filters.Items.Add(groupFilter); filters.Items.Add(new MenuFlyoutSeparator()); filters.Items.Add(clearFilters);
        filterButton.Flyout = filters;
        unreadFilter.Click += (_, _) => Render(); groupFilter.Click += (_, _) => Render();
        clearFilters.Click += (_, _) => { ClearConversationFilters(); Render(); };
        Grid.SetColumn(filterButton, 1); searchRow.Children.Add(filterButton); Grid.SetRow(searchRow, 1); sidebar.Children.Add(searchRow);
        chatList.ItemsSource = chats; chatList.ItemTemplate = Template("ChatRow");
        chatList.ItemContainerStyle = ContainerStyle();
        chatList.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["ConversationListPanel"];
        chatList.SelectionChanged += async (_, _) => { if (!updatingConversationList && chatList.SelectedItem is ChatItem item && item.Guid != Client.Active) await SelectAsync(item.Guid); };
        chatList.ItemClick += async (_, args) => { if (args.ClickedItem is ChatItem item) { if (item.Guid != Client.Active) await SelectAsync(item.Guid); else Scroll(); } };
        Grid.SetRow(chatList, 2); sidebar.Children.Add(chatList); Grid.SetRow(emptyChats, 2); sidebar.Children.Add(emptyChats);
        var footer = new Grid { Margin = new(12), ColumnSpacing = 6 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(status); var settings = Button("Settings", () => _ = SettingsAsync()); Grid.SetColumn(settings, 1); footer.Children.Add(settings);
        var recovery = new StackPanel { Name = "ConnectionRecovery", Margin = new(12, 0, 12, 0), Spacing = 8 };
        error.Name = "ConnectionError";
        recovery.Children.Add(error);
        recoveryActions.Children.Add(retryConnection); recoveryActions.Children.Add(restartServer);
        recovery.Children.Add(recoveryActions);
        retryConnection.Click += async (_, _) => await RecoverAsync(false);
        restartServer.Click += async (_, _) => await RecoverAsync(true);
        Grid.SetRow(recovery, 3); sidebar.Children.Add(recovery);
        Grid.SetRow(footer, 4); sidebar.Children.Add(footer); root.Children.Add(sidebar);
        var content = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) content.RowDefinitions.Add(new() { Height = height });
        var header = new Grid { Padding = new(20, 16, 20, 16), ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.Children.Add(avatar); var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; name.Children.Add(heading); name.Children.Add(subheading); Grid.SetColumn(name, 1); header.Children.Add(name);
        content.Children.Add(new Border { Name = "ConversationHeader", Child = header, Style = (Style)Application.Current.Resources["ConversationHeaderStyle"] });
        var notices = new StackPanel(); notices.Children.Add(updateBar);
        updateBar.ActionButton = installUpdate;
        installUpdate.Click += async (_, _) => await UpdatesAsync(false);
        Grid.SetRow(notices, 1); content.Children.Add(notices);
        messageList.ItemsSource = messages; messageList.ItemTemplate = Template("MessageRow"); messageList.ItemContainerStyle = ContainerStyle();
        messageList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((sender, args) =>
        {
            var properties = args.GetCurrentPoint(messageList).Properties;
            if (properties.IsHorizontalMouseWheel) return;
            if (messageViewer?.ScrollableHeight > 0 && (properties.MouseWheelDelta > 0 || !LatestVisible())) CancelAutoScroll();
            else { followLatest = true; userScrolling = false; }
        }), true);
        messageList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((sender, args) =>
        {
            if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) CancelAutoScroll();
            for (var source = args.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
                if (source is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) { CancelAutoScroll(); break; }
        }), true);
        messageList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((sender, args) =>
        {
            if (userScrolling && LatestVisible()) { followLatest = true; userScrolling = false; }
        }), true);
        messageList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((sender, args) =>
        {
            if (args.Key == VirtualKey.End) { Scroll(); return; }
            if (args.Key is VirtualKey.Down or VirtualKey.PageDown && LatestVisible()) return;
            if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End) CancelAutoScroll();
        }), true);
        messageList.Loaded += (_, _) => Scroll(); Grid.SetRow(messageList, 2); content.Children.Add(messageList);
        Grid.SetRow(historyProgress, 2); content.Children.Add(historyProgress);
        dashboard.OpenConversation += guid => { search.Text = ""; _ = SelectAndRevealAsync(guid); };
        dashboard.NewConversation += () => _ = NewChatAsync();
        Grid.SetRow(dashboard, 2); content.Children.Add(dashboard);
        entry.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); entry.RowDefinitions.Add(new() { Height = GridLength.Auto }); entry.RowDefinitions.Add(new() { Height = GridLength.Auto });
        entry.Children.Add(composer); send.Click += async (_, _) => await SendAsync(); Grid.SetColumn(send, 1); entry.Children.Add(send);
        var hint = new TextBlock { Text = "Ctrl+Enter to send · Enter for a new line · Drafts saved locally", FontSize = 11, Opacity = .6 }; Grid.SetRow(hint, 1); Grid.SetColumnSpan(hint, 2); entry.Children.Add(hint); Grid.SetRow(entry, 3); content.Children.Add(entry);
        Grid.SetColumn(content, 1); root.Children.Add(content); Content = root;
        search.TextChanged += (_, _) => Render();
        composer.TextChanged += (_, _) => { if (!changingDraft && draftGuid is { } guid) Client.Draft(guid, composer.Text); };
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        composer.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
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
        var wasFollowing = FollowingLatest;
        var previous = state;
        state = Client.Snapshot();
        var namesChanged = previous.Contacts.Count != state.Contacts.Count || previous.Contacts.Any(p => state.Contacts.GetValueOrDefault(p.Key) != p.Value);
        Title = (state.Unread.Count > 0 ? $"({state.Unread.Count}) " : "") + $"NG Bubbles v{AppInfo.Version}";
        tray?.Update(state.Unread.Count);
        status.Text = state.Status switch { "connected" => "Connected to your Mac", "connecting" => "Connecting…", _ => "Offline · reconnecting" };
        UpdateRecoveryControls();
        if (!recovering)
        {
            if (state.Error.Length > 0) ShowError(state.Error);
            else if (error.Message == previous.Error) error.IsOpen = false;
        }
        var activeFilters = new List<string>();
        if (unreadFilter.IsChecked) activeFilters.Add("Unread");
        if (groupFilter.IsChecked) activeFilters.Add("Groups");
        filterCount.Text = activeFilters.Count.ToString(); filterCount.Visibility = activeFilters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        clearFilters.IsEnabled = activeFilters.Count > 0;
        var filterDescription = activeFilters.Count > 0 ? "Filters: " + string.Join(", ", activeFilters) : "Filter conversations";
        ToolTipService.SetToolTip(filterButton, filterDescription);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(filterButton, filterDescription);
        var wanted = ConversationFilters.Apply(state, search.Text, unreadFilter.IsChecked, groupFilter.IsChecked);
        emptyChats.Text = activeFilters.Count > 0 || search.Text.Length > 0 ? "No conversations match your search and filters." : "No conversations yet.";
        emptyChats.Visibility = wanted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        updatingConversationList = true;
        try
        {
            var wantedIds = wanted.Select(c => c.Guid).ToHashSet();
            for (var i = chats.Count - 1; i >= 0; i--) if (!wantedIds.Contains(chats[i].Guid)) chats.RemoveAt(i);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (!rows.TryGetValue(wanted[i].Guid, out var row)) { row = new(wanted[i], state); rows[row.Guid] = row; } else row.Update(wanted[i], state);
                var old = chats.IndexOf(row); if (old < 0) chats.Insert(i, row); else if (old != i) chats.Move(old, i);
            }
            var selectedChat = chats.FirstOrDefault(c => c.Guid == state.Active);
            if (!ReferenceEquals(chatList.SelectedItem, selectedChat)) chatList.SelectedItem = selectedChat;
        }
        finally { updatingConversationList = false; }
        if (state.Active != draftGuid)
        {
            draftGuid = state.Active; changingDraft = true; composer.Text = draftGuid == null ? "" : Client.Draft(draftGuid); changingDraft = false;
            messages.Clear(); messageRows.Clear(); scrollBottom = true;
        }
        var current = state.Chats.FirstOrDefault(c => c.Guid == state.Active);
        heading.Text = current == null ? "Home" : Model.Title(current, state.Contacts);
        subheading.Text = current == null ? "Your conversations at a glance" : string.Join(", ", current.Participants.Select(p => p.Address));
        avatar.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        dashboard.Visibility = current == null ? Visibility.Visible : Visibility.Collapsed;
        entry.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        messageList.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        if (current == null) dashboard.Update(state, Client.Drafts());
        if (current != null) avatar.Set(current, state);
        composer.IsEnabled = current != null; send.IsEnabled = current != null && !sending;
        historyProgress.IsActive = state.Active is { } loading && loadingHistory.Contains(loading);
        historyProgress.Visibility = historyProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
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
        if (scrollBottom || wasFollowing && lastBefore != messages.LastOrDefault()?.Message.Guid) { Scroll(); scrollBottom = false; }
    }
    private static string Signature(Message message) => message.Guid + "|" + string.Join(';', message.Reactions.Select(reaction => reaction.EventGuid + reaction.Emoji + reaction.Sender + reaction.IsFromMe)) + "|" + message.AssociatedMessageGuid + "|" + message.AssociatedMessageType + "|" + message.AssociatedMessageEmoji + "|" + message.BalloonBundleId + "|" + message.PayloadData?.GetRawText() + "|" + message.Service + "|" + message.ChatGuid + "|" + message.Error + "|" + message.Text + "|" + message.DateDelivered + "|" + message.DateRead + "|" + message.IsRead + "|" + string.Join(',', message.Attachments.Select(a => a.Guid + a.TransferName + a.MimeType + a.TotalBytes));
    public bool FollowingLatest => followLatest;
    public void MediaLayoutChanged()
    {
        if (followLatest) MaintainScroll();
    }
    private void CancelAutoScroll()
    {
        scrollVersion++; scrolling = false; scrollBottom = false; followLatest = false; userScrolling = true;
    }
    private bool LatestVisible()
    {
        if (messageViewer == null || messages.Count == 0) return true;
        if (messageViewer.ScrollableHeight - messageViewer.VerticalOffset > 2) return false;
        if (messageList.ContainerFromIndex(messages.Count - 1) is not FrameworkElement last || last.ActualHeight <= 0) return false;
        var bottom = last.TransformToVisual(messageList).TransformPoint(new(0, last.ActualHeight)).Y;
        return bottom <= messageList.ActualHeight + 2;
    }
    private void MaintainScroll()
    {
        if (quitting || !AppWindow.IsVisible || state.Active == null) return;
        if (messageViewer == null)
        {
            messageViewer = Visuals(messageList).OfType<ScrollViewer>().FirstOrDefault();
            if (messageViewer == null) return;
            messageViewer.BringIntoViewOnFocusChange = false;
            messageViewer.ViewChanged += (_, args) =>
            {
                if (scrolling || !userScrolling) return;
                followLatest = messageViewer.ScrollableHeight - messageViewer.VerticalOffset <= 2;
                if (!args.IsIntermediate) userScrolling = false;
            };
        }
        if (followLatest && !scrolling && !LatestVisible()) Scroll();
        if (!followLatest && !scrolling && messageViewer.VerticalOffset < 240 && state.Active is { } guid &&
            !loadingHistory.Contains(guid) && Environment.TickCount64 >= historyRetryAt.GetValueOrDefault(guid) && Client.CanLoadOlder(guid))
            _ = LoadOlderAsync();
    }
    private void Scroll()
    {
        followLatest = true; userScrolling = false;
        if (messages.Count == 0) return;
        var version = ++scrollVersion;
        scrolling = true;
        DispatcherQueue.TryEnqueue(() => _ = ScrollToLatestAsync(version));
    }
    private async Task ScrollToLatestAsync(int version)
    {
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(50);
                if (version != scrollVersion || quitting || messages.Count == 0) return;
                messageList.UpdateLayout();
                messageList.ScrollIntoView(messages[^1], ScrollIntoViewAlignment.Leading);
                messageList.UpdateLayout();
                messageViewer?.ChangeView(null, messageViewer.ScrollableHeight, null, true);
            }
        }
        finally { if (version == scrollVersion) scrolling = false; }
    }
    private void ClearConversationFilters()
    {
        unreadFilter.IsChecked = false; groupFilter.IsChecked = false;
    }
    private void ShowHome()
    {
        if (draftGuid is { } previous) Client.Draft(previous, composer.Text);
        CancelAutoScroll();
        Client.Home();
        ClearConversationFilters(); search.Text = "";
        Render();
        home.Focus(FocusState.Programmatic);
    }
    private async Task SelectAsync(string guid)
    {
        if (draftGuid is { } previous) Client.Draft(previous, composer.Text);
        scrollBottom = true;
        followLatest = true; userScrolling = false;
        Client.Select(guid);
        guid = Client.Active!;
        await RunAsync(() => Client.OpenAsync(guid));
        if (Client.Active == guid) { Render(); Scroll(); composer.Focus(FocusState.Programmatic); }
    }
    private async Task LoadOlderAsync()
    {
        if (state.Active is not { } guid || messages.Count == 0 || !Client.CanLoadOlder(guid) || !loadingHistory.Add(guid)) return;
        var index = messageList.ItemsPanelRoot is ItemsStackPanel panel ? Math.Max(0, panel.FirstVisibleIndex) : 0;
        var anchor = messages[Math.Min(index, messages.Count - 1)];
        var top = messageList.ContainerFromItem(anchor) is FrameworkElement container ? container.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y : 0;
        CancelAutoScroll(); userScrolling = false;
        var version = scrollVersion;
        historyProgress.IsActive = true; historyProgress.Visibility = Visibility.Visible;
        try
        {
            var before = messages[0].Message.DateCreated;
            await Task.Run(() => Client.OpenAsync(guid, before));
            if (Client.Active != guid || version != scrollVersion) return;
            historyRetryAt[guid] = Client.Snapshot().Messages.FirstOrDefault()?.DateCreated < before ? 0 : Environment.TickCount64 + 30000;
            Render();
            var row = messages.FirstOrDefault(item => item.Message.Guid == anchor.Message.Guid);
            if (row == null) return;
            messageList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            await Task.Delay(50);
            if (Client.Active != guid || version != scrollVersion) return;
            messageList.UpdateLayout();
            if (messageViewer != null && messageList.ContainerFromItem(row) is FrameworkElement restored)
            {
                var currentTop = restored.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y;
                messageViewer.ChangeView(null, messageViewer.VerticalOffset + currentTop - top, null, true);
            }
        }
        catch (Exception e)
        {
            historyRetryAt[guid] = Environment.TickCount64 + 30000;
            Data.Log.Log("history-load-failed", new { type = e.GetType().Name });
        }
        finally
        {
            loadingHistory.Remove(guid);
            historyProgress.IsActive = Client.Active is { } active && loadingHistory.Contains(active);
            historyProgress.Visibility = historyProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        }
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
            ClearConversationFilters(); search.Text = "";
            await SelectAsync(guid);
            var row = chats.FirstOrDefault(c => c.Guid == Client.Active);
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
            if (Client.Active == guid) { Render(); Scroll(); }
        }
        catch (Exception e) { Data.Log.Log("send-failed", new { type = e.GetType().Name }); ShowError("Send was not confirmed. Your draft is kept. Check delivery before sending again."); }
        finally { sending = false; send.IsEnabled = draftGuid != null; composer.Focus(FocusState.Programmatic); }
    }
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Data.Log.Log("operation-failed", new { type = e.GetType().Name }); ShowError(e is IOException or ArgumentException ? e.Message : "The operation failed. Open Diagnostics for technical details."); }
    }
    private void ShowError(string text) { error.Severity = InfoBarSeverity.Warning; error.Message = text; error.IsOpen = true; }
    private void UpdateRecoveryControls()
    {
        recoveryActions.Visibility = state.Status == "offline" ? Visibility.Visible : Visibility.Collapsed;
        retryConnection.IsEnabled = !recovering;
        restartServer.IsEnabled = !recovering;
        if (recovering && recoveryConnecting && state.Status == "connected" && error.Severity == InfoBarSeverity.Informational) error.IsOpen = false;
    }
    private StackPanel SettingsRecoveryActions(Action<bool> selected)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = Button("Retry connection", () => selected(false));
        var restart = Button("Restart server", () => selected(true));
        retry.IsEnabled = !recovering; restart.IsEnabled = !recovering;
        actions.Children.Add(retry); actions.Children.Add(restart);
        return actions;
    }
    private async Task RecoverAsync(bool restart)
    {
        if (recovering) return;
        if (Data.Settings.Server.Length == 0 || Data.Password().Length == 0) { await SettingsAsync(); return; }
        if (restart)
        {
            if (dialogOpen) return;
            dialogOpen = true;
            try
            {
                var confirmation = new ContentDialog { Title = "Restart BlueBubbles server?", Content = "This briefly disconnects all clients. The app will try your local address and tunnel, then reconnect automatically. The Mac must be awake and BlueBubbles must still be responding.",
                    PrimaryButtonText = "Restart server", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot };
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally { dialogOpen = false; }
        }
        recovering = true;
        retryConnection.IsEnabled = false; restartServer.IsEnabled = false;
        error.Severity = InfoBarSeverity.Informational;
        error.Message = restart ? "Requesting server restart…" : "Reconnecting…"; error.IsOpen = true;
        try
        {
            var settings = Data.Settings; var secret = Data.Password();
            if (restart)
            {
                using var connection = new Api(settings.Server, secret, Data.Log, localServer: settings.LocalServer);
                try { await connection.RestartAsync(windowLifetime.Token); }
                catch (IOException) { throw new IOException("The restart was not confirmed. Retry the connection before restarting again. Check the local address and port in Settings; the Mac must be awake and BlueBubbles running."); }
                error.Message = "Server restart requested. Reconnecting…";
                await Task.Delay(3000, windowLifetime.Token);
            }
            recoveryConnecting = true;
            await Task.Run(() => Client.ConnectAsync(settings, secret));
            for (var attempt = 0; restart && attempt < 10 && Client.Snapshot().Status != "connected"; attempt++)
            {
                await Task.Delay(3000, windowLifetime.Token);
                await Task.Run(() => Client.SyncAsync(windowLifetime.Token));
            }
            Render();
            if (state.Status != "connected") throw new IOException("Still unable to connect. Check the local address and port in Settings, and make sure your VPN or home network can reach the Mac.");
            error.IsOpen = state.Error.Length > 0;
            if (error.IsOpen) ShowError(state.Error);
            Data.Log.Log("connection-recovered", new { restarted = restart });
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            Data.Log.Log("connection-recovery-failed", new { type = e.GetType().Name });
            ShowError(e is IOException or ArgumentException ? e.Message : "Recovery failed. See Diagnostics for details.");
        }
        finally { recovering = false; recoveryConnecting = false; UpdateRecoveryControls(); }
    }
    public AttachmentViewer ShowAttachment(Attachment attachment, string path)
    {
        var viewer = new AttachmentViewer(attachment, path);
        attachmentWindows.Add(viewer);
        viewer.Closed += (_, _) => attachmentWindows.Remove(viewer);
        viewer.Activate();
        return viewer;
    }
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
            var localUri = Uri.TryCreate(current.LocalServer, UriKind.Absolute, out var savedLocal) ? savedLocal : null;
            var localHost = new TextBox { Header = "Local Mac address (optional)", Text = localUri?.GetLeftPart(UriPartial.Authority) ?? "", PlaceholderText = "http://macbook.internal" };
            var localPort = new NumberBox { Header = "Local server port", Value = localUri?.Port ?? 1234, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var password = new PasswordBox { Header = "Server password", PlaceholderText = "Leave blank to keep saved password" };
            var method = new ComboBox { Header = "Sending method", ItemsSource = new[] { "Standard API", "Private API" }, SelectedIndex = current.SendMethod == "private-api" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var enabled = new ToggleSwitch { Header = "Notifications", IsOn = current.Notifications };
            var preview = new ToggleSwitch { Header = "Message previews in notifications", IsOn = current.Preview };
            var startup = new ToggleSwitch { Header = "Start with Windows", IsOn = current.LaunchAtLogin };
            var connection = new StackPanel { Spacing = 14 };
            connection.Children.Add(server); connection.Children.Add(localHost); connection.Children.Add(localPort);
            connection.Children.Add(new TextBlock { Text = "Use your Mac's local address on your home network or VPN, with the port shown in BlueBubbles Server (usually 1234). Both addresses must point to the same Mac.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            connection.Children.Add(password); connection.Children.Add(method);
            connection.Children.Add(new TextBlock { Text = "Messages are automatically marked read on your Mac when Private API is available.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            var preferencesPanel = new StackPanel { Spacing = 14 };
            preferencesPanel.Children.Add(enabled); preferencesPanel.Children.Add(preview); preferencesPanel.Children.Add(startup);
            var test = Button("Test notification", () => { if (!notifications.Available) ShowError("Windows notification registration failed. See Diagnostics."); else notifications.Show(new("", "NG Bubbles", "Notifications are working."), true); }); preferencesPanel.Children.Add(test);
            bool? recoveryAction = null;
            var openUpdates = false;
            var openDiagnostics = false;
            var refresh = false;
            var support = new StackPanel { Spacing = 14 };
            support.Children.Add(new TextBlock { Text = $"NG Bubbles v{AppInfo.Version}", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var updatesButton = new Button { Content = "Check for updates" }; support.Children.Add(updatesButton);
            var diagnosticsButton = new Button { Content = "Open diagnostics" }; support.Children.Add(diagnosticsButton);
            var refreshButton = new Button { Content = "Refresh conversations and photos" }; support.Children.Add(refreshButton);
            support.Children.Add(new TextBlock { Text = "Connection recovery", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var tabs = new SettingsTabs(connection, preferencesPanel, support, root.XamlRoot.Size.Width, root.XamlRoot.Size.Height);
            var dialog = new ContentDialog { Title = "NG Bubbles settings", Content = tabs, PrimaryButtonText = "Save and connect", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot };
            dialog.Resources["ContentDialogMaxWidth"] = tabs.Width + 48;
            support.Children.Add(SettingsRecoveryActions(restart => { recoveryAction = restart; dialog.Hide(); }));
            support.Children.Add(new TextBlock { Text = "Recovery uses your saved connection settings.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            updatesButton.Click += (_, _) => { openUpdates = true; dialog.Hide(); };
            diagnosticsButton.Click += (_, _) => { openDiagnostics = true; dialog.Hide(); };
            refreshButton.Click += (_, _) => { refresh = true; dialog.Hide(); };
            var showing = dialog.ShowAsync();
            if (smoke) await SettingsSmokeTestAsync(dialog, tabs, server, enabled);
            var result = await showing;
            if (refresh) { Client.RequestSync(); await RunAsync(() => Task.Run(Client.RefreshPhotosAsync)); return; }
            if (recoveryAction.HasValue) { dialogOpen = false; await RecoverAsync(recoveryAction.Value); return; }
            if (openUpdates) { dialogOpen = false; await UpdatesAsync(true); return; }
            if (openDiagnostics) { dialogOpen = false; await DiagnosticsAsync(); return; }
            if (result != ContentDialogResult.Primary) return;
            await RunAsync(async () =>
            {
                var origin = Model.NormalizeServer(server.Text);
                var secret = password.Password.Length > 0 ? password.Password : Data.Password();
                if (secret.Length == 0) throw new IOException("Enter your server password.");
                var local = "";
                if (!string.IsNullOrWhiteSpace(localHost.Text))
                {
                    if (double.IsNaN(localPort.Value) || localPort.Value != Math.Truncate(localPort.Value) || localPort.Value is < 1 or > 65535) throw new ArgumentException("Enter a local port between 1 and 65535.");
                    var localOrigin = Model.NormalizeServer(localHost.Text.Contains("://") ? localHost.Text : "http://" + localHost.Text);
                    local = new UriBuilder(localOrigin) { Port = (int)localPort.Value }.Uri.GetLeftPart(UriPartial.Authority);
                }
                var preferences = new Preferences { Server = origin, LocalServer = local, SendMethod = method.SelectedIndex == 1 ? "private-api" : "apple-script", Notifications = enabled.IsOn, Preview = preview.IsOn, LaunchAtLogin = startup.IsOn };
                using var check = new Api(origin, secret, Data.Log, localServer: local); await check.RequestAsync("/ping");
                if (origin != current.Server && current.Server.Length > 0) throw new IOException("Changing servers requires a separate Windows data folder to avoid mixing conversations. Keep the existing server for this update.");
                await Data.SaveSettingsAsync(preferences, secret);
                ConfigureStartup();
                await Task.Run(() => Client.ConnectAsync(preferences, secret));
            });
        }
        finally { dialogOpen = false; }
    }
    private async Task SettingsSmokeTestAsync(ContentDialog dialog, SettingsTabs view, TextBox server, ToggleSwitch notificationsEnabled)
    {
        try
        {
            await Task.Delay(150); root.UpdateLayout();
            var tabs = view.Tabs;
            var expected = new[] { "Connection", "Preferences", "Shortcuts", "Support" };
            if (!tabs.TabItems.Cast<TabViewItem>().Select(item => item.Header as string).SequenceEqual(expected) ||
                tabs.IsAddTabButtonVisible || tabs.CanDragTabs || tabs.CanReorderTabs || tabs.TabItems.Cast<TabViewItem>().Any(item => item.IsClosable))
                throw new InvalidOperationException("Settings tabs are missing or expose editing controls.");
            server.Text = "https://settings.example.test/" + new string('a', 160);
            await Task.Delay(80); root.UpdateLayout();
            var bounds = view.TransformToVisual(root).TransformBounds(new(0, 0, view.ActualWidth, view.ActualHeight));
            var footer = Visuals(dialog).OfType<Button>().Where(button => button.Content as string is "Save and connect" or "Close").ToArray();
            if (footer.Length != 2 || Math.Abs(view.ActualWidth - view.Width) > 1 || view.ActualWidth > root.ActualWidth - 48)
                throw new InvalidOperationException($"Settings does not fit its fixed size within the window: footer={footer.Length}, actual={view.ActualWidth}, requested={view.Width}, window={root.ActualWidth}.");
            var footerBounds = footer.Select(button => button.TransformToVisual(root).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight))).ToArray();
            notificationsEnabled.IsOn = false;
            for (var index = 0; index < expected.Length * 2; index++)
            {
                tabs.SelectedIndex = index % expected.Length; await Task.Delay(80); root.UpdateLayout();
                var currentBounds = view.TransformToVisual(root).TransformBounds(new(0, 0, view.ActualWidth, view.ActualHeight));
                if (Math.Abs(currentBounds.X - bounds.X) > 1 || Math.Abs(currentBounds.Y - bounds.Y) > 1 ||
                    Math.Abs(currentBounds.Width - bounds.Width) > 1 || Math.Abs(currentBounds.Height - bounds.Height) > 1 ||
                    footer.Where((button, position) =>
                    {
                        var current = button.TransformToVisual(root).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
                        var previous = footerBounds[position];
                        return Math.Abs(current.X - previous.X) > 1 || Math.Abs(current.Y - previous.Y) > 1 ||
                            Math.Abs(current.Width - previous.Width) > 1 || Math.Abs(current.Height - previous.Height) > 1;
                    }).Any())
                    throw new InvalidOperationException("Switching Settings tabs moved or resized the dialog or its footer.");
                if (tabs.SelectedItem is not TabViewItem item || item.Header as string != expected[index % expected.Length] || item.Content is not ScrollViewer)
                    throw new InvalidOperationException("Settings tab selection did not retain its content.");
                if (index == 2 && !Visuals((DependencyObject)item.Content).OfType<TextBlock>().Any(label => label.Text == "Ctrl+Shift+D"))
                    throw new InvalidOperationException("The Shortcuts tab does not list diagnostics.");
                if (index == 3 && (!Visuals((DependencyObject)item.Content).OfType<Button>().Any(button => button.Content as string == "Restart server") ||
                    !Visuals((DependencyObject)item.Content).OfType<Button>().Any(button => button.Content as string == "Refresh conversations and photos")))
                    throw new InvalidOperationException("The Support tab does not provide server recovery.");
            }
            tabs.SelectedIndex = 0; root.UpdateLayout();
            if (server.Text != "https://settings.example.test/" + new string('a', 160) || notificationsEnabled.IsOn)
                throw new InvalidOperationException("Changing settings tabs lost pending field edits.");
            if (root.KeyboardAcceleratorPlacementMode != KeyboardAcceleratorPlacementMode.Hidden || composer.KeyboardAcceleratorPlacementMode != KeyboardAcceleratorPlacementMode.Hidden ||
                !root.KeyboardAccelerators.Any(shortcut => shortcut.Key == VirtualKey.D && shortcut.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)))
                throw new InvalidOperationException("Shortcut hints are not hidden or the diagnostics binding was removed.");
            tabs.SelectedIndex = 2; await Task.Delay(80); root.UpdateLayout();
            if (Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_REPORT") is { } report)
                await CapturePreviewAsync(view, Path.Combine(Path.GetDirectoryName(report)!, "ng-settings.png"));
            Data.Log.Log("settings-tabs-passed", new { tabs = 4, shortcuts = true, support = true, editsRetained = true, hintsHidden = true, bindingRetained = true, fixedLayout = true });
        }
        finally { dialog.Hide(); }
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
            updateBar.Message = "Download the update and restart NG Bubbles when you're ready.";
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
        Title = "NG Bubbles updates", Content = new TextBlock { Text = updateMessage + (release == null ? "" : "\n\nYour drafts, settings and window size will be saved. The update will install without setup prompts, then NG Bubbles will reopen."), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
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
            if (Process.Start(ReleaseUpdater.InstallerStartInfo(installer, AppContext.BaseDirectory, Environment.ProcessId)) == null) throw new IOException("The installer could not be started.");
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
        if (tray == null) throw new InvalidOperationException("Tray icon is unavailable.");
        tray.SmokeTest(); tray.Update(state.Unread.Count);
        if (Visuals(root).OfType<Button>().Any(button => button.Content as string == "Sync"))
            throw new InvalidOperationException("The redundant Sync button remains in the main interface.");
        Data.Log.Log("tray-tooltip-passed", new { standardTooltip = true, counts = true, explorerRestart = true, headerSyncRemoved = true });
        AppWindow.Resize(new(Math.Max(640, AppWindow.Size.Width - 80), Math.Max(480, AppWindow.Size.Height - 60)));
        var savedWindow = new WindowSettings(0, 0);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            savedWindow = Data.Store.Load("window.json", () => new WindowSettings(0, 0));
            if (Math.Abs(savedWindow.Width * windowState.Scale() - AppWindow.Size.Width) <= 2 && !savedWindow.Maximized) break;
        }
        if (Math.Abs(savedWindow.Width * windowState.Scale() - AppWindow.Size.Width) > 2 || savedWindow.Maximized)
            throw new InvalidOperationException("Resized window dimensions were not saved.");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
            var maximized = new WindowSettings();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100);
                maximized = Data.Store.Load("window.json", () => new WindowSettings());
                if (maximized.Maximized) break;
            }
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
        Client.Focused = false;
        await SelectAsync(fixture["guid"]);
        App.Trace("smoke-chat-opened");
        await Task.Delay(2500);
        Render();
        App.Trace("smoke-validate");
        var connectedState = state;
        if (state.Status != "connected" || recoveryActions.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Recovery buttons should be hidden while connected.");
        var settingsRecovery = SettingsRecoveryActions(_ => { });
        if (settingsRecovery.Children.OfType<Button>().Count(b => b.Visibility == Visibility.Visible && b.IsEnabled) != 2) throw new InvalidOperationException("Settings must retain both recovery actions while connected.");
        state = state with { Status = "offline" }; UpdateRecoveryControls();
        ShowError("Synthetic connection warning"); root.UpdateLayout();
        if (recoveryActions.Visibility != Visibility.Visible || recoveryActions.ActualHeight <= 0) throw new InvalidOperationException("Recovery buttons must be visible when offline.");
        var recoveryPanel = (StackPanel)error.Parent;
        var recoverySidebar = (Grid)recoveryPanel.Parent;
        if (Grid.GetColumn(recoverySidebar) != 0 || Grid.GetRow(recoveryPanel) != 3 || error.ActualWidth > 300 ||
            recoverySidebar.Children.OfType<Grid>().All(child => Grid.GetRow(child) != 4) ||
            retryConnection.Parent != restartServer.Parent || !retryConnection.IsEnabled || !restartServer.IsEnabled)
            throw new InvalidOperationException("Connection recovery controls are not above the sidebar footer.");
        state = state with { Status = "connecting" }; UpdateRecoveryControls();
        if (recoveryActions.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Recovery buttons should be hidden during initial connection.");
        state = connectedState; recovering = true; recoveryConnecting = true;
        error.Severity = InfoBarSeverity.Informational; error.Message = "Reconnecting…"; error.IsOpen = true;
        UpdateRecoveryControls(); root.UpdateLayout();
        if (error.IsOpen || recoveryActions.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Connected recovery must remove its progress notice and sidebar buttons.");
        if (SettingsRecoveryActions(_ => { }).Children.OfType<Button>().Any(b => b.IsEnabled)) throw new InvalidOperationException("Recovery actions must not start a second operation while busy.");
        recovering = false; recoveryConnecting = false; UpdateRecoveryControls();
        Data.Log.Log("connection-recovery-layout-passed", new { sidebar = true, aboveFooter = true, retry = true, restart = true, hiddenConnected = true, visibleOffline = true, settingsActions = true, progressCleared = true });
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
        if (!Client.Snapshot().Unread.Contains(fixture["guid"])) throw new InvalidOperationException("The server unread conversation was cleared while unfocused.");
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
        var viewer = Visuals(messageList).OfType<ScrollViewer>().First();
        if (viewer.ScrollableHeight < 100) throw new InvalidOperationException("Scroll acceptance requires an overflowing conversation.");
        CancelAutoScroll();
        viewer.ChangeView(null, viewer.ScrollableHeight / 2, null, true);
        await Task.Delay(150);
        await SelectAsync(fixture["guid"]);
        await Task.Delay(500);
        if (!FollowingLatest || viewer.ScrollableHeight - viewer.VerticalOffset > 2) throw new InvalidOperationException("Opening a conversation did not reach its bottom.");
        CancelAutoScroll();
        viewer.ChangeView(null, viewer.ScrollableHeight / 2, null, true);
        await Task.Delay(150);
        composer.Text = "Sent while reading older history.";
        await SendAsync();
        await Task.Delay(500);
        if (messages.LastOrDefault()?.Message.Text != "Sent while reading older history." || viewer.ScrollableHeight - viewer.VerticalOffset > 2)
            throw new InvalidOperationException("Sending a message did not reveal the last sent message at the bottom.");
        Data.Log.Log("conversation-scroll-passed", new { opening = true, sending = true, messages = messages.Count });
        await AttachmentSmokeTestAsync();
        await AttachmentChromeSmokeTestAsync();
        await TransportSmokeTestAsync(fixture["guid"]);
        await AppearanceSmokeTestAsync();
        await ScrollingSmokeTestAsync(fixture["guid"]);
        await NotificationAvatarSmokeTestAsync(fixture["guid"]);
        await MessageContentSmokeTestAsync(fixture["guid"]);

        Data.Log.Log("ui-smoke-passed", new { conversations = chats.Count, messages = messages.Count, nativeText = true, notifications = notifications.Available, tray = tray != null });
        await ConversationFiltersSmokeTestAsync(fixture["guid"]);
        await SettingsAsync();
        await HomeSmokeTestAsync(fixture["guid"]);
        var target = Environment.GetEnvironmentVariable("NG_BUBBLES_SMOKE_REPORT");
        if (target != null)
        {
            await File.WriteAllTextAsync(target, Data.Log.Report());
            App.Trace("smoke-capture");
            await CapturePreviewAsync(root, Path.ChangeExtension(target, ".png"));
            await File.WriteAllTextAsync(target, Data.Log.Report());
        }
        App.Trace("smoke-quit");
        await QuitAsync();
    }
    private static async Task CapturePreviewAsync(UIElement visual, string target)
    {
        var capture = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await capture.RenderAsync(visual);
        var pixels = await capture.GetPixelsAsync();
        using var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)capture.PixelWidth, (uint)capture.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        using var result = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await result.LoadAsync((uint)stream.Size); var png = new byte[stream.Size]; result.ReadBytes(png);
        await File.WriteAllBytesAsync(target, png);
    }
    private async Task MessageContentSmokeTestAsync(string guid)
    {
        var sample = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(sample, 1); root.Children.Add(sample);
        try
        {
            foreach (var outgoing in new[] { false, true })
            {
                sample.Children.Clear();
                const string value = "See https://example.test/survey?N=002832738-10.\nAlso www.example.com/plans, mailto:person@example.com, tel:+15551234567 and custom-app://open/item.";
                var row = new MessageRow { DataContext = new MessageItem(new Message { Text = value, IsFromMe = outgoing }, state) };
                sample.Children.Add(row); await Task.Delay(50); root.UpdateLayout();
                var text = Visuals(row).OfType<TextBlock>().Single(block => block.IsTextSelectionEnabled);
                var links = text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>().ToList();
                if (links.Count != 5 || Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(links[0]) != "https://example.test/survey?N=002832738-10" || Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(links[1]) != "https://www.example.com/plans" || new Uri(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(links[2])).Scheme != "mailto" || new Uri(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(links[3])).Scheme != "tel" || new Uri(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(links[4])).Scheme != "custom-app")
                    throw new InvalidOperationException("Message links lost their web destinations or query parameters.");
                var restored = string.Concat(text.Inlines.Select(inline => inline is Microsoft.UI.Xaml.Documents.Run run ? run.Text :
                    string.Concat(((Microsoft.UI.Xaml.Documents.Hyperlink)inline).Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(part => part.Text))));
                if (restored != value || outgoing && links.Any(link => ((SolidColorBrush)link.Foreground).Color != Microsoft.UI.Colors.White))
                    throw new InvalidOperationException("Link rendering changed message text or outgoing link contrast.");
                sample.Children.Clear();
                row = new MessageRow { DataContext = new MessageItem(new Message { Text = "sure", IsFromMe = outgoing,
                    Reactions = [new("🤦", "alex@example.test", false, "one"), new("👍", "sam@example.test", false, "two")] }, state) };
                sample.Children.Add(row); await Task.Delay(50); root.UpdateLayout();
                var bubble = Visuals(row).OfType<Border>().Single(border => border.Name == "MessageBubble");
                var badge = Visuals(row).OfType<Border>().Single(border => border.Name == "MessageReactions");
                var label = Visuals(bubble).OfType<TextBlock>().Single();
                var badgeBounds = badge.TransformToVisual(root).TransformBounds(new(0, 0, badge.ActualWidth, badge.ActualHeight));
                var bubbleBounds = bubble.TransformToVisual(root).TransformBounds(new(0, 0, bubble.ActualWidth, bubble.ActualHeight));
                if (Math.Abs(bubble.ActualWidth - label.ActualWidth - 24) > 2 || badge.ActualHeight <= 0 || badge.ActualHeight > 32 ||
                    badgeBounds.Y >= bubbleBounds.Y || badgeBounds.X > bubbleBounds.X + 9 || badgeBounds.Right > root.ActualWidth + 1)
                    throw new InvalidOperationException("Reaction badges moved away from the top-left of their message or widened its bubble.");
            }
        }
        finally { root.Children.Remove(sample); }
        Client.Focused = false;
        var reaction = new Message { Guid = "synthetic-live-reaction", ChatGuid = guid, AssociatedMessageGuid = "p:0/synthetic-short-outgoing", AssociatedMessageType = 2006,
            AssociatedMessageEmoji = "🤦", Handle = new("alex@example.test"), DateCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Text = "Reacted 🤦 to “sure”" };
        Client.Accept(reaction, notify: false, updateUnread: false); Render();
        var attached = messages.Single(item => item.Message.Guid == "synthetic-short-outgoing");
        if (attached.Message.Reactions.SingleOrDefault()?.Emoji != "🤦" || messages.Any(item => item.Message.Guid == reaction.Guid))
            throw new InvalidOperationException("A live reaction did not update its original message row.");
        Client.Accept(reaction with { Guid = "synthetic-removed-reaction", AssociatedMessageType = 3006, DateCreated = reaction.DateCreated + 1, Text = "Removed 🤦 from “sure”" }, notify: false, updateUnread: false); Render();
        if (messages.Single(item => item.Message.Guid == "synthetic-short-outgoing").Message.Reactions.Count != 0)
            throw new InvalidOperationException("A removed reaction remained attached to the message.");
        Data.Log.Log("message-links-passed", new { destinations = true, queries = true, textPreserved = true, contrast = true, mailto = true, tel = true, customSchemes = true });
        Data.Log.Log("message-reactions-passed", new { overlay = true, naturalBubbleWidth = true, liveAdd = true, liveRemove = true });
    }
    private async Task ConversationFiltersSmokeTestAsync(string guid)
    {
        Client.Focused = false;
        await SelectAsync(guid);
        composer.Text = "Draft retained while filtering";
        filterButton.Flyout.ShowAt(filterButton); await Task.Delay(100);
        void Toggle(ToggleMenuFlyoutItem item) => ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)
            new Microsoft.UI.Xaml.Automation.Peers.ToggleMenuFlyoutItemAutomationPeer(item).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
        Toggle(unreadFilter); root.UpdateLayout();
        if (!unreadFilter.IsChecked || chats.Count == 0 || chats.Any(chat => !state.Unread.Contains(chat.Guid)) || filterCount.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Unread filtering or the active filter indicator failed.");
        filterButton.Flyout.ShowAt(filterButton); await Task.Delay(80);
        Toggle(groupFilter); root.UpdateLayout();
        if (chats.Count == 0 || chats.Any(chat => !chat.Chat.IsGroup || !state.Unread.Contains(chat.Guid)) || filterCount.Text != "2")
            throw new InvalidOperationException("Combined unread and group filters failed.");
        search.Text = "not-a-matching-conversation"; await Task.Delay(80); root.UpdateLayout();
        if (chats.Count != 0 || emptyChats.Visibility != Visibility.Visible || Client.Active != guid || composer.Text != "Draft retained while filtering")
            throw new InvalidOperationException($"Filtering lost state: count={chats.Count}, empty={emptyChats.Visibility}, activeMatches={Client.Active == guid}, draftMatches={composer.Text == "Draft retained while filtering"}.");
        search.Text = "Weekend"; await Task.Delay(80); root.UpdateLayout();
        if (chats.Count != 1 || chats[0].Guid != "synthetic-group") throw new InvalidOperationException("Search does not combine with filters.");
        search.Text = "";
        filterButton.Flyout.ShowAt(filterButton); await Task.Delay(80);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer(clearFilters)
            .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        root.UpdateLayout();
        if (unreadFilter.IsChecked || groupFilter.IsChecked || chats.Count != state.Chats.Count || filterCount.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Clear filters did not restore all conversations.");
        filterButton.Flyout.Hide();
        groupFilter.IsChecked = true; search.Text = "non-matching"; Render();
        await SelectAndRevealAsync(guid);
        if (unreadFilter.IsChecked || groupFilter.IsChecked || search.Text.Length != 0 || chatList.SelectedItem is not ChatItem selected || selected.Guid != guid || composer.Text != "Draft retained while filtering")
            throw new InvalidOperationException("Opening a conversation did not clear filters and reveal its row while preserving the draft.");
        composer.Text = "";
        Data.Log.Log("conversation-filters-passed", new { unread = true, groups = true, combinedSearch = true, emptyState = true, draftPreserved = true, clear = true, reveal = true });
    }
    private async Task HomeSmokeTestAsync(string guid)
    {
        Client.Focused = false;
        await SelectAsync(guid);
        var draft = "A draft kept when returning home";
        composer.Text = draft;
        var unread = Client.Snapshot().Unread.ToHashSet();
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(home).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(150); root.UpdateLayout();
        if (Client.Active != null || chatList.SelectedItem != null || dashboard.Visibility != Visibility.Visible ||
            entry.Visibility != Visibility.Collapsed || messageList.Visibility != Visibility.Collapsed ||
            dashboard.Summary?.ConversationCount != state.Chats.Count || dashboard.Summary.DraftCount < 1 ||
            !unread.SetEquals(Client.Snapshot().Unread) || Client.Draft(guid) != draft)
            throw new InvalidOperationException("Home dashboard did not preserve draft, unread status and navigation.");
        var draftCard = Visuals(dashboard).OfType<Button>().Single(button => button.Tag as string == "Saved drafts:" + guid);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(draftCard).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        for (var attempt = 0; attempt < 40 && (dashboard.Visibility != Visibility.Collapsed || composer.Text != draft); attempt++) await Task.Delay(50);
        if (dashboard.Visibility != Visibility.Collapsed || entry.Visibility != Visibility.Visible || composer.Text != draft)
            throw new InvalidOperationException("Opening a conversation from Home did not restore the composer and draft.");
        composer.Text = "";
        ShowHome();
        AppWindow.Resize(new(1280, 800));
        dashboard.Width = 1000;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(50); root.UpdateLayout();
            if (Visuals(dashboard).OfType<Grid>().Any(grid => grid.ColumnDefinitions.Count == 2 && grid.Children.OfType<StackPanel>().Count() == 3)) break;
        }
        Data.Log.Log("home-dashboard-layout", new { window = AppWindow.Size.Width, width = dashboard.ActualWidth,
            grids = Visuals(dashboard).OfType<Grid>().Select(grid => new { width = grid.ActualWidth, columns = grid.ColumnDefinitions.Count, sections = grid.Children.OfType<StackPanel>().Count() }).ToArray() });
        if (!Visuals(dashboard).OfType<Grid>().Any(grid => grid.ColumnDefinitions.Count == 2 && grid.Children.OfType<StackPanel>().Count() == 3))
            throw new InvalidOperationException("Home dashboard did not arrange its sections for a wide viewport.");
        dashboard.Width = double.NaN; await Task.Delay(100); root.UpdateLayout();
        var sections = Visuals(dashboard).OfType<Grid>().Single(grid => grid.Children.OfType<StackPanel>().Count() == 3);
        if (sections.ColumnDefinitions.Count != (sections.ActualWidth >= 720 ? 2 : 1)) throw new InvalidOperationException("Home dashboard did not restore its layout for the available width.");
        Data.Log.Log("home-dashboard-passed", new { summary = true, navigation = true, draftPreserved = true, unreadPreserved = true, composerHidden = true, responsive = true });
    }
    private async Task AttachmentSmokeTestAsync()
    {
        var sample = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(sample, 1); root.Children.Add(sample);
        var originalActive = Client.Active;
        try
        {
            var anchor = new TextBlock { Text = "Attachment layout anchor" };
            sample.Children.Add(anchor);
            var portrait = new AttachmentPreview(new Attachment { Guid = "synthetic-portrait", TransferName = "portrait.png", MimeType = "image/png" });
            var landscape = new AttachmentPreview(new Attachment { Guid = "synthetic-landscape", TransferName = "landscape.png", MimeType = "image/png" });
            sample.Children.Add(portrait); sample.Children.Add(landscape);
            root.UpdateLayout();
            var before = sample.ActualHeight;
            var anchorTop = anchor.TransformToVisual(root).TransformPoint(new(0, 0)).Y;
            if (portrait.Ready.IsCompleted || landscape.Ready.IsCompleted || portrait.ActualWidth != AttachmentPreview.PreviewWidth)
                throw new InvalidOperationException("Attachment placeholders did not reserve their width before delayed downloads.");
            await Task.WhenAll(portrait.Ready, landscape.Ready).WaitAsync(TimeSpan.FromSeconds(15));
            root.UpdateLayout();
            if (!portrait.Available || !landscape.Available || Math.Abs(sample.ActualHeight - before) > .5 ||
                Math.Abs(anchor.TransformToVisual(root).TransformPoint(new(0, 0)).Y - anchorTop) > .5)
                throw new InvalidOperationException("Downloaded attachment changed message height or displaced the reading anchor.");
            foreach (var preview in new[] { portrait, landscape })
            {
                var image = Visuals(preview).OfType<Image>().Single();
                if (preview.ActualWidth != 300 || preview.ActualHeight != 220 || image.Stretch != Stretch.Uniform)
                    throw new InvalidOperationException("Portrait or landscape preview escaped its fixed frame.");
            }
            var count = (await Client.Connection.RequestAsync("/test/attachment-requests")).GetArrayLength();
            var button = Visuals(portrait).OfType<Button>().Single(b => b.Content is Image);
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            for (var attempt = 0; attempt < 40 && attachmentWindows.Count == 0; attempt++) await Task.Delay(25);
            var viewer = attachmentWindows.Single();
            await viewer.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            if (!viewer.Available || viewer.AppWindow.Size.Width < 600 || Client.Active != originalActive)
                throw new InvalidOperationException("Clicking the image did not open an enlarged independent viewer.");
            if ((await Client.Connection.RequestAsync("/test/attachment-requests")).GetArrayLength() != count)
                throw new InvalidOperationException("Enlarging a cached image downloaded it again.");
            viewer.Close();
            if (attachmentWindows.Count != 0) throw new InvalidOperationException("Attachment viewer did not close independently.");
            sample.Children.Clear();
            var failed = new AttachmentPreview(new Attachment { Guid = "synthetic-unavailable", TransferName = "unavailable.jpg", MimeType = "image/jpeg" });
            sample.Children.Add(failed); root.UpdateLayout(); var failureHeight = failed.ActualHeight;
            await failed.Ready.WaitAsync(TimeSpan.FromSeconds(10)); root.UpdateLayout();
            if (failed.Available || Math.Abs(failureHeight - failed.ActualHeight) > .5)
                throw new InvalidOperationException("A failed attachment preview changed its reserved height.");
            Data.Log.Log("attachment-layout-passed", new { width = 300, height = 220, orientations = 2, delayed = true,
                stableAnchor = true, failureStable = true, popout = true, cached = true, closed = true });
        }
        finally
        {
            foreach (var viewer in attachmentWindows.ToArray()) viewer.Close();
            root.Children.Remove(sample); Activate(); root.UpdateLayout(); Scroll();
        }
    }
    private async Task AttachmentChromeSmokeTestAsync()
    {
        var sample = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(sample, 1); root.Children.Add(sample);
        var images = new[] {
            new Attachment { Guid = "synthetic-portrait", TransferName = "portrait.png", MimeType = "image/png" },
            new Attachment { Guid = "synthetic-landscape", TransferName = "landscape.png", MimeType = "image/png" }
        };
        try
        {
            var row = new MessageRow { DataContext = new MessageItem(new Message { Attachments = [..images] }, state) };
            sample.Children.Add(row); root.UpdateLayout();
            var tiles = Visuals(row).OfType<AttachmentPreview>().ToArray();
            await Task.WhenAll(tiles.Select(tile => tile.Ready)); root.UpdateLayout();
            if (tiles.Length != 2 || tiles.Any(tile => tile.ActualWidth != 146 || tile.ActualHeight != 146))
                throw new InvalidOperationException("Multiple photos did not get individual square tiles.");
            var first = tiles[0].TransformToVisual(sample).TransformPoint(new(0, 0));
            var second = tiles[1].TransformToVisual(sample).TransformPoint(new(0, 0));
            if (Math.Abs(first.Y - second.Y) > .5 || second.X - first.X < 150)
                throw new InvalidOperationException("Photo tiles overlapped or lost their separating gap.");
            foreach (var tile in tiles)
            {
                var download = Visuals(tile).OfType<Button>().Single(button => button.Content is SymbolIcon);
                if (download.ActualWidth > 36 || !Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(download).StartsWith("Download ") ||
                    Visuals(tile).OfType<TextBlock>().Any(text => text.Text.Contains(".png") || text.Text.StartsWith("Save ")))
                    throw new InvalidOperationException("Photo download control exposed a filename or lost its accessible name.");
            }
            var selected = new List<string>();
            foreach (var attachment in images)
            {
                sample.Children.Clear();
                var tile = new AttachmentPreview(attachment, 146, true, value => { selected.Add(value.Guid); return Task.CompletedTask; });
                sample.Children.Add(tile); await tile.Ready; root.UpdateLayout();
                var button = Visuals(tile).OfType<Button>().Single(control => control.Content is SymbolIcon);
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await Task.Delay(25);
            }
            if (!selected.SequenceEqual(images.Select(image => image.Guid))) throw new InvalidOperationException("Download icon selected the wrong photo.");
            sample.Children.Clear();
            var message = Client.Snapshot().Messages.Single(value => value.Guid == "synthetic-rich-link");
            var linkRow = new MessageRow { DataContext = new MessageItem(message, state) };
            sample.Children.Add(linkRow); root.UpdateLayout(); var height = linkRow.ActualHeight;
            var link = Visuals(linkRow).OfType<LinkPreview>().Single();
            await link.Ready.WaitAsync(TimeSpan.FromSeconds(15)); root.UpdateLayout();
            var target = Visuals(link).OfType<HyperlinkButton>().Single();
            if (target.NavigateUri?.Host != "example.test" || !Visuals(link).OfType<TextBlock>().Any(text => text.Text == "Weekend plans") ||
                !Visuals(link).OfType<Image>().Any() || Visuals(linkRow).OfType<AttachmentPreview>().Any() ||
                Visuals(linkRow).OfType<TextBlock>().Any(text => text.Text.Contains("pluginPayloadAttachment")) || Math.Abs(linkRow.ActualHeight - height) > .5)
                throw new InvalidOperationException("Rich link did not become one stable clickable preview instead of internal attachment files.");
            Data.Log.Log("attachment-controls-passed", new { iconOnly = true, squareTiles = 2, separateDownloads = true, richLink = true, linkImage = true, stableLink = true });
        }
        finally { root.Children.Remove(sample); root.UpdateLayout(); Scroll(); }
    }
    private async Task TransportSmokeTestAsync(string guid)
    {
        var aliases = state.Chats.Where(c => !c.IsGroup && c.Participants.Any(p => p.Address == "+15551234567")).ToList();
        if (aliases.Count != 1 || aliases[0].Guid != guid) throw new InvalidOperationException("SMS and iMessage appeared as separate conversations.");
        var sample = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(sample, 1); root.Children.Add(sample);
        try
        {
            foreach (var service in new[] { "iMessage", "SMS", "RCS" })
            {
                sample.Children.Clear();
                var row = new MessageRow { DataContext = new MessageItem(new Message
                {
                    Text = "Transport color", IsFromMe = true, Service = service,
                    ChatGuid = guid, DateCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, state) };
                sample.Children.Add(row); root.UpdateLayout();
                var bubble = ((StackPanel)row.Content).Children.OfType<Border>().Single();
                var color = ((SolidColorBrush)bubble.Background).Color;
                if (service == "iMessage" && color.B <= color.G || service != "iMessage" && color.G <= color.B)
                    throw new InvalidOperationException("Message transport did not control its bubble color.");
            }
        }
        finally { root.Children.Remove(sample); root.UpdateLayout(); }
        var original = Client.Active;
        Client.Select("SMS;-;+15551234567");
        if (Client.Active != guid) throw new InvalidOperationException("An SMS notification did not resolve to the shared conversation.");
        if (original != null) Client.Select(original);
        Data.Log.Log("conversation-transports-passed", new { singleThread = true, services = 3, notificationAlias = true });
    }
    private async Task AppearanceSmokeTestAsync()
    {
        var header = Visuals(root).OfType<Border>().Single(border => border.Name == "ConversationHeader");
        var originalTheme = root.RequestedTheme;
        var sample = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(sample, 1); root.Children.Add(sample);
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(100); root.UpdateLayout();
                var color = ((SolidColorBrush)header.Background).Color;
                if (color == ((SolidColorBrush)root.Background).Color || header.BorderThickness.Bottom < 1 ||
                    theme == ElementTheme.Light && color.R != 228 || theme == ElementTheme.Dark && color.R != 28)
                    throw new InvalidOperationException("Conversation header did not retain its distinct theme background and divider.");
            }
            root.RequestedTheme = ElementTheme.Light;
            foreach (var outgoing in new[] { false, true })
            {
                foreach (var text in new[] { "Hi", string.Join(" ", Enumerable.Repeat("A longer wrapped message.", 20)) })
                {
                    sample.Children.Clear();
                    var row = new MessageRow { DataContext = new MessageItem(new Message
                    {
                        Text = text, IsFromMe = outgoing, DateCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        DateDelivered = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Handle = new("synthetic@example.test")
                    }, state) };
                    sample.Children.Add(row); root.UpdateLayout();
                    var outer = (StackPanel)row.Content;
                    var bubble = outer.Children.OfType<Border>().Single();
                    var body = (StackPanel)bubble.Child;
                    var label = body.Children.OfType<TextBlock>().Single();
                    if (bubble.ActualWidth <= 0 || Math.Abs(bubble.ActualWidth - label.ActualWidth - 24) > 2 ||
                        text == "Hi" && bubble.ActualWidth >= outer.Children.OfType<TextBlock>().Last().ActualWidth ||
                        text.Length > 100 && label.ActualHeight < 40)
                        throw new InvalidOperationException("Message bubble does not fit its text independently of sender and timestamp.");
                }
            }
        }
        finally { root.Children.Remove(sample); root.RequestedTheme = originalTheme; root.UpdateLayout(); }
        await BubbleListSmokeTestAsync();
        Data.Log.Log("conversation-appearance-passed", new { themes = 2, bubbleCases = 4 });
    }
    private async Task BubbleListSmokeTestAsync()
    {
        var normalSize = AppWindow.Size;
        try
        {
            foreach (var width in new[] { 900, 1400 })
            {
                AppWindow.Resize(new(width, normalSize.Height));
                await Task.Delay(150);
                foreach (var guid in new[] { "synthetic-short-incoming", "synthetic-short-outgoing" })
                {
                    var original = messages.Single(row => row.Message.Guid == guid);
                    var index = messages.IndexOf(original);
                    try
                    {
                        foreach (var receipt in new[] { 0, 1, 2 })
                        {
                            var item = new MessageItem(original.Message with
                            {
                                DateDelivered = receipt > 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
                                DateRead = receipt == 2 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null
                            }, original.State);
                            messages[index] = item;
                            Scroll();
                            await Task.Delay(350); root.UpdateLayout();
                            if (messageList.ContainerFromItem(item) is not DependencyObject container)
                                throw new InvalidOperationException("Short message was not realized in the conversation list.");
                            var row = Visuals(container).OfType<MessageRow>().Single();
                            var bubble = ((StackPanel)row.Content).Children.OfType<Border>().Single();
                            var label = ((StackPanel)bubble.Child).Children.OfType<TextBlock>().Single();
                            var natural = new TextBlock { Text = item.Message.Text, FontSize = label.FontSize, FontFamily = label.FontFamily };
                            natural.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
                            if (Math.Abs(bubble.ActualWidth - natural.DesiredSize.Width - 24) > 2)
                                throw new InvalidOperationException("Virtualized bubble includes width beyond its natural text and padding.");
                        }
                    }
                    finally { messages[index] = original; }
                }
            }
        }
        finally { AppWindow.Resize(normalSize); await Task.Delay(150); Scroll(); }
        Data.Log.Log("bubble-list-width-passed", new { widths = 2, directions = 2, receiptStates = 3, naturalTextWidth = true });
    }
    private async Task ScrollingSmokeTestAsync(string guid)
    {
        await Task.Delay(350);
        var viewer = messageViewer ?? throw new InvalidOperationException("Conversation scroll viewer is missing.");
        var last = (DependencyObject)messageList.ContainerFromIndex(messages.Count - 1);
        var row = Visuals(last).OfType<MessageRow>().Single();
        var body = (StackPanel)((StackPanel)row.Content).Children.OfType<Border>().Single().Child;
        var delayed = new Border { Height = 340 };
        await Task.Delay(700);
        body.Children.Add(delayed); MediaLayoutChanged();
        await Task.Delay(750); root.UpdateLayout();
        if (!LatestVisible()) throw new InvalidOperationException("Delayed message layout moved the latest message off screen.");
        CancelAutoScroll(); viewer.ChangeView(null, viewer.ScrollableHeight / 2, null, true);
        await Task.Delay(250);
        var manualIndex = ((ItemsStackPanel)messageList.ItemsPanelRoot).FirstVisibleIndex;
        var manualAnchor = messages[manualIndex];
        var manualTop = ((FrameworkElement)messageList.ContainerFromItem(manualAnchor)).TransformToVisual(messageList).TransformPoint(new(0, 0)).Y;
        var offset = viewer.VerticalOffset;
        delayed.Height = 500; MediaLayoutChanged();
        for (var i = 0; i < 10; i++) { Render(); await Task.Delay(50); }
        var manualContainer = messageList.ContainerFromItem(manualAnchor) as FrameworkElement;
        var displacement = manualContainer == null ? double.MaxValue : manualContainer.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y - manualTop;
        Data.Log.Log("manual-scroll-position", new { offsetChange = viewer.VerticalOffset - offset, displacement, following = FollowingLatest });
        if (FollowingLatest || Math.Abs(displacement) > 2)
            throw new InvalidOperationException("A delayed image or refresh overrode manual conversation scrolling.");
        paint.Stop();
        body.Children.Remove(delayed);
        root.UpdateLayout(); await Task.Delay(100); root.UpdateLayout();
        var firstVisible = messages[0];
        var count = messages.Count;
        CancelAutoScroll();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            viewer.ChangeView(null, 0, null, true);
            await Task.Delay(25); root.UpdateLayout();
            if (viewer.VerticalOffset <= 2) break;
        }
        var firstContainer = (FrameworkElement)messageList.ContainerFromItem(firstVisible);
        var firstTop = firstContainer.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y;
        Data.Log.Log("history-scroll-start", new { offset = viewer.VerticalOffset, count, canLoad = Client.CanLoadOlder(guid), following = FollowingLatest });
        if (viewer.VerticalOffset > 2) throw new InvalidOperationException("The test did not finish its scroll to the top.");
        paint.Start();
        for (var attempt = 0; attempt < 200 && (messages.Count == count || loadingHistory.Contains(guid)); attempt++) await Task.Delay(25);
        await Task.Delay(250); root.UpdateLayout();
        var restored = (FrameworkElement)messageList.ContainerFromItem(messages.Single(item => item.Message.Guid == firstVisible.Message.Guid));
        Data.Log.Log("history-scroll-finish", new { count = messages.Count, previous = count, displacement = restored.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y - firstTop,
            following = FollowingLatest, offset = viewer.VerticalOffset, canLoad = Client.CanLoadOlder(guid) });
        if (messages.Count <= count || Math.Abs(restored.TransformToVisual(messageList).TransformPoint(new(0, 0)).Y - firstTop) > 2 || FollowingLatest)
            throw new InvalidOperationException("Infinite scrolling did not load older history and preserve the visible message.");
        if (Client.CanLoadOlder(guid) || Visuals(root).OfType<Button>().Any(button => button.Content as string == "Load older messages"))
            throw new InvalidOperationException("History completion or removal of the older-messages button failed.");
        Data.Log.Log("infinite-history-passed", new { automatic = true, anchorPreserved = true, endReached = true, buttonRemoved = true });
        var sidebar = Visuals(chatList).OfType<ScrollViewer>().First();
        sidebar.ChangeView(null, sidebar.ScrollableHeight / 2, null, true);
        await Task.Delay(250);
        var sidebarOffset = sidebar.VerticalOffset;
        for (var i = 0; i < 10; i++) { Render(); await Task.Delay(25); }
        if (sidebar.ScrollableHeight < 500 || Math.Abs(sidebar.VerticalOffset - sidebarOffset) > 2)
            throw new InvalidOperationException("Conversation sidebar moved during a background refresh.");
        ActivateChat(guid);
        await Task.Delay(750); root.UpdateLayout();
        if (chatList.ContainerFromItem(chatList.SelectedItem) is not FrameworkElement selected)
            throw new InvalidOperationException("Notification did not reveal the selected conversation.");
        var bounds = selected.TransformToVisual(chatList).TransformBounds(new(0, 0, selected.ActualWidth, selected.ActualHeight));
        if (bounds.Top < -2 || bounds.Bottom > chatList.ActualHeight + 2 || !LatestVisible())
            throw new InvalidOperationException("Notification did not reveal both conversation and latest message.");
        Data.Log.Log("manual-scrolling-passed", new { delayedLayout = true, userScrollPreserved = true, olderHistory = true, sidebarRefresh = true, notificationReveal = true });
    }
    private async Task NotificationAvatarSmokeTestAsync(string contactGuid)
    {
        var snapshot = Client.Snapshot();
        var contact = snapshot.Chats.Single(chat => chat.Guid == contactGuid);
        var photo = Model.Photo(contact.Participants[0].Address, snapshot.ContactPhotos);
        var group = snapshot.Chats.Single(chat => chat.Guid == "synthetic-group");
        async Task<string> LogoAsync(Chat chat, Snapshot source)
        {
            var toast = await notifications.BuildAsync(new(chat.Guid, "Synthetic notification", "Synthetic body"), source);
            var xml = System.Xml.Linq.XDocument.Parse(toast.Payload);
            var image = xml.Descendants("image").Single(element => (string?)element.Attribute("placement") == "appLogoOverride");
            var path = new Uri((string)image.Attribute("src")!).LocalPath;
            if (!File.Exists(path) || !toast.Payload.Contains("chat=")) throw new InvalidOperationException("Notification avatar or conversation activation argument is missing.");
            using var stream = await (await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth < 44 || decoder.PixelHeight < 44) throw new InvalidOperationException("Notification avatar is not a usable image.");
            return path;
        }
        if (await LogoAsync(contact, snapshot) != photo) throw new InvalidOperationException("Contact notification did not use its cached photo.");
        var shared = snapshot with { GroupPhotos = new() { [group.Guid] = photo } };
        if (await LogoAsync(group, shared) != photo) throw new InvalidOperationException("Group notification did not prefer its shared photo.");
        foreach (var count in new[] { 2, 3, 4 })
        {
            var members = group.Participants.Concat(Enumerable.Range(0, 4).Select(i => new Participant($"member{i}@example.test"))).Take(count).ToList();
            var chat = group with { Participants = members };
            var source = snapshot with { Chats = [chat], GroupPhotos = [] };
            var path = await LogoAsync(chat, source);
            if (path == photo || path != await LogoAsync(chat, source)) throw new InvalidOperationException("Group notification did not cache its member portrait collage.");
        }
        await LogoAsync(contact, snapshot with { ContactPhotos = [] });
        var missing = await notifications.BuildAsync(new("missing", "Unknown conversation", "Body"), snapshot);
        if (System.Xml.Linq.XDocument.Parse(missing.Payload).Descendants("image").Any()) throw new InvalidOperationException("Missing conversation used an unrelated avatar.");
        Data.Log.Log("notification-avatars-passed", new { contact = true, sharedGroup = true, memberGroups = 3, fallback = true });
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
