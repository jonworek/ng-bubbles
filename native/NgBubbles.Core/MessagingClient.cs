using System.Text.Json;
using System.Threading.Channels;

namespace NgBubbles.Core;

public sealed class MessagingClient : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly SemaphoreSlim contactGate = new(1, 1);
    private readonly HashSet<string> reads = [];
    private readonly Diagnostics log;
    private readonly Store store;
    private readonly Func<JsonElement, Task<string>> photo;
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource lifetime = new();
    private CancellationTokenSource? saveDelay;
    private Api? api;
    private LiveConnection? live;
    private Preferences preferences = new();
    private string status = "offline";
    private string error = "";
    private string readError = "";
    private long contactsAt;
    private long groupsAt;
    public ClientCache Cache { get; }
    public string? Active { get; private set; }
    public bool Focused { get; set; }
    public event Action? Changed;
    public event Action<IncomingNotification>? Notification;
    public MessagingClient(Store store, Diagnostics log, Func<JsonElement, Task<string>> photo)
    {
        this.store = store; this.log = log; this.photo = photo;
        Cache = store.Load("cache.json", () => new ClientCache());
    }
    public Snapshot Snapshot()
    {
        lock (gate) return new(Cache.Chats.OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).ToList(),
            Cache.Histories.GetValueOrDefault(Active ?? "")?.ToList() ?? [], Active, [..Cache.Unread], new(Cache.Contacts),
            new(Cache.ContactPhotos), new(Cache.GroupPhotos), status, error.Length > 0 ? error : readError);
    }
    public async Task ConnectAsync(Preferences settings, string password)
    {
        lifetime.Cancel(); lifetime.Dispose();
        lifetime = new();
        var token = lifetime.Token;
        preferences = settings;
        var connection = new Api(settings.Server, password, log);
        api?.Dispose(); api = connection;
        contactsAt = 0; groupsAt = 0;
        lock (gate) { status = "connecting"; error = ""; }
        Publish();
        live = new(settings.Server, password, log);
        _ = live.RunAsync((name, data) => LiveAsync(connection, name, data, token), () => { if (connection == api) RequestSync(); }, token);
        _ = PollAsync(connection, token);
        _ = SignalsAsync(connection, token);
        _ = ContactsAsync(connection, token);
        await SyncAsync(token);
    }
    private async Task PollAsync(Api connection, CancellationToken token)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20)); while (await timer.WaitForNextTickAsync(token)) { if (api != connection) return; RequestSync(); } }
        catch (OperationCanceledException) { }
    }
    private async Task SignalsAsync(Api connection, CancellationToken token)
    {
        try
        {
            while (await signals.Reader.WaitToReadAsync(token))
            {
                while (signals.Reader.TryRead(out _)) { }
                if (connection != api) return;
                try { await SyncAsync(token); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void RequestSync() => signals.Writer.TryWrite(true);
    public async Task SyncAsync(CancellationToken token = default)
    {
        var connection = api;
        if (connection == null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await syncGate.WaitAsync(token);
        try
        {
            var chats = await connection.PagesAsync<Chat>("/chat/query", token: token);
            var where = new[] { new { statement = "message.is_from_me = :fromMe AND message.is_read = :read", args = new { fromMe = 0, read = 0 } } };
            var unread = await connection.PagesAsync<Message>("/message/query", where, token: token);
            long cursor;
            lock (gate) cursor = Cache.Cursor;
            var recovered = await connection.PagesAsync<Message>("/message/query", after: Math.Max(0, cursor - 3000), token: token);
            string? active;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                Cache.Chats = chats;
                var serverUnread = unread.SelectMany(m => m.Chats.Select(c => (c.Guid, message: m))).ToList();
                Cache.Unread = serverUnread.Where(x => !Cache.ReadStates.TryGetValue(x.Guid, out var read) || !Covered(read, x.message)) .Select(x => x.Guid).ToHashSet();
                foreach (var state in Cache.ReadStates.ToList())
                {
                    if (state.Value.ManualUnread) Cache.Unread.Add(state.Key);
                    if (!state.Value.LocalOnly && !state.Value.ManualUnread && !serverUnread.Any(x => x.Guid == state.Key)) Cache.ReadStates.Remove(state.Key);
                }
                active = Active;
            }
            foreach (var message in recovered) Accept(message, notify: true, updateUnread: false);
            lock (gate) Cache.Cursor = Math.Max(cursor, recovered.Select(m => m.DateCreated).DefaultIfEmpty(cursor).Max());
            if (active != null) await LoadHistoryAsync(connection, active, null, token);
            List<string> pending;
            lock (gate) pending = Cache.ReadStates.Where(s => s.Value.LocalOnly && !s.Value.ManualUnread).Select(s => s.Key).ToList();
            if (active != null && Focused) await ReadAsync(active, token);
            foreach (var guid in pending) await ReadAsync(guid, token);
            lock (gate) { status = "connected"; error = live?.Connected == true ? "" : "Live connection reconnecting; checking every 20 seconds."; }
            Publish(); QueueSave();
            if (Environment.TickCount64 - contactsAt > 300000) _ = ContactsAsync(connection, token);
            if (Environment.TickCount64 - groupsAt > 300000) _ = GroupsAsync(connection, token);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) return;
            lock (gate) { status = "offline"; error = e is IOException ? e.Message : "Connection interrupted. Retrying automatically."; }
            log.Log("sync-failed", new { type = e.GetType().Name }); Publish();
        }
        finally { syncGate.Release(); }
    }
    public void Accept(Message message, bool notify = true, bool updateUnread = true)
    {
        IncomingNotification? notification = null;
        lock (gate)
        {
            if (message.Guid.Length == 0) return;
            var guid = message.Chats.FirstOrDefault()?.Guid ?? message.ChatGuid ?? Cache.Chats.FirstOrDefault(c => c.LastMessage?.Guid == message.Guid)?.Guid
                ?? Cache.Histories.FirstOrDefault(p => p.Value.Any(m => m.Guid == message.Guid)).Key;
            if (string.IsNullOrEmpty(guid)) return;
            var known = Cache.Seen.Contains(message.Guid);
            Cache.Seen.Add(message.Guid);
            Cache.Histories[guid] = Model.Merge(Cache.Histories.GetValueOrDefault(guid) ?? [], [message]);
            var chat = Cache.Chats.FirstOrDefault(c => c.Guid == guid);
            if (chat == null) { chat = message.Chats.FirstOrDefault() ?? new Chat { Guid = guid }; Cache.Chats.Add(chat); }
            if (message.DateCreated >= (chat.LastMessage?.DateCreated ?? 0)) chat.LastMessage = message;
            if (updateUnread && !known && !message.IsFromMe && (!Cache.ReadStates.TryGetValue(guid, out var read) || !Covered(read, message))) Cache.Unread.Add(guid);
            if (notify && !known && !message.IsFromMe && message.DateCreated > Cache.Cursor - 3000 && !(Active == guid && Focused) && Cache.Unread.Contains(guid))
                notification = new(guid, Model.Title(chat, Cache.Contacts), message.Preview);
        }
        if (notification != null) Notification?.Invoke(notification);
        QueueSave(); Publish();
    }
    private async Task LiveAsync(Api connection, string name, JsonElement data, CancellationToken token)
    {
        if (connection != api || token.IsCancellationRequested) return;
        try
        {
            if (name is "new-message" or "updated-message")
            {
                if (data.Deserialize<Message>(Json.Options) is { } message) Accept(message, name == "new-message");
                if (Active is { } guid && Focused) await ReadAsync(guid, token);
            }
            else if (name == "chat-read-status-changed" && data.TryGetProperty("chatGuid", out var guid) && data.TryGetProperty("read", out var read))
            {
                lock (gate)
                {
                    if (read.ValueKind == JsonValueKind.True) { Cache.Unread.Remove(guid.GetString()!); SetReadState(guid.GetString()!, false); }
                    else if (read.ValueKind == JsonValueKind.False) { Cache.ReadStates.Remove(guid.GetString()!); Cache.Unread.Add(guid.GetString()!); }
                }
                Publish(); QueueSave();
            }
            if (name is "group-icon-changed" or "group-icon-removed") { groupsAt = 0; }
            RequestSync();
        }
        catch (Exception e) when (e is JsonException or IOException or OperationCanceledException) { log.Log("live-event-failed", new { type = e.GetType().Name }); }
    }
    public async Task OpenAsync(string guid, long? before = null)
    {
        lock (gate) { if (before == null) Active = guid; }
        Publish();
        var connection = api;
        try { if (connection != null) await LoadHistoryAsync(connection, guid, before, lifetime.Token); }
        finally { if (before == null && Focused) await ReadAsync(guid); Publish(); QueueSave(); }
    }
    private async Task LoadHistoryAsync(Api connection, string guid, long? before, CancellationToken token)
    {
        var json = await connection.RequestAsync($"/chat/{Uri.EscapeDataString(guid)}/message", query: new() { ["with"] = "attachments,handle", ["sort"] = "DESC", ["limit"] = "100", ["before"] = before?.ToString() }, token: token);
        var messages = json.Deserialize<List<Message>>(Json.Options) ?? [];
        lock (gate) { token.ThrowIfCancellationRequested(); Cache.Histories[guid] = Model.Merge(Cache.Histories.GetValueOrDefault(guid) ?? [], messages); }
    }
    private static bool Covered(ReadState state, Message message) => !state.ManualUnread &&
        (state.LocalOnly || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.ConfirmedAt < 5000) &&
        (message.DateCreated < state.Through || message.DateCreated == state.Through && state.Guids.Contains(message.Guid));
    private ReadState SetReadState(string guid, bool local)
    {
        var history = Cache.Histories.GetValueOrDefault(guid) ?? [];
        var last = Cache.Chats.FirstOrDefault(c => c.Guid == guid)?.LastMessage;
        var through = Math.Max(last?.DateCreated ?? 0, history.Select(m => m.DateCreated).DefaultIfEmpty(0).Max());
        var state = new ReadState { Through = through, LocalOnly = local, ConfirmedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Guids = history.Where(m => m.DateCreated == through).Select(m => m.Guid).Concat(last?.DateCreated == through ? [last.Guid] : []).ToHashSet() };
        Cache.ReadStates[guid] = state;
        return state;
    }
    public async Task ReadAsync(string guid, CancellationToken token = default)
    {
        ReadState? state;
        Api? connection;
        lock (gate)
        {
            if (Cache.Unread.Remove(guid)) SetReadState(guid, true);
            state = Cache.ReadStates.GetValueOrDefault(guid);
            connection = api;
            if (state is not { LocalOnly: true } || connection == null || !reads.Add(guid)) return;
        }
        Publish(); QueueSave();
        try
        {
            var info = await connection.RequestAsync("/server/info", token: token);
            if (!info.TryGetProperty("private_api", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            { lock (gate) readError = "Read locally. Enable Private API on the Mac to synchronize read status."; log.Log("read-unavailable"); return; }
            await connection.RequestAsync($"/chat/{Uri.EscapeDataString(guid)}/read", post: true, token: token);
            lock (gate)
            {
                if (connection != api) return;
                if (ReferenceEquals(Cache.ReadStates.GetValueOrDefault(guid), state)) { state.LocalOnly = false; state.ConfirmedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                readError = "";
            }
            log.Log("read-confirmed"); QueueSave();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        { lock (gate) readError = "Read locally. Server read sync failed; retrying automatically."; log.Log("read-retry"); }
        finally { lock (gate) reads.Remove(guid); Publish(); }
    }
    public async Task SendAsync(string guid, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) throw new IOException("Enter a message up to 20,000 characters.");
        var connection = api ?? throw new IOException("Connect to your server first.");
        var result = await connection.RequestAsync("/message/text", new { chatGuid = guid, message = text, tempGuid = Guid.NewGuid().ToString(), method = preferences.SendMethod }, token: lifetime.Token);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("guid", out _)) Accept(result.Deserialize<Message>(Json.Options)! with { IsFromMe = true, ChatGuid = guid }, false);
        RequestSync();
    }
    public async Task<string> CreateChatAsync(string[] addresses, string text, string service)
    {
        if (addresses.Length is < 1 or > 32 || addresses.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 254) || string.IsNullOrWhiteSpace(text) || text.Length > 20000 || (service != "iMessage" && service != "SMS")) throw new IOException("Enter recipients and an initial message.");
        var connection = api ?? throw new IOException("Connect first.");
        var result = await connection.RequestAsync("/chat/new", new { addresses, message = text, service, method = preferences.SendMethod }, token: lifetime.Token);
        await SyncAsync(lifetime.Token);
        return result.GetProperty("guid").GetString()!;
    }
    public string Draft(string guid) { lock (gate) return Cache.Drafts.GetValueOrDefault(guid) ?? ""; }
    public void Draft(string guid, string text) { lock (gate) { if (text.Length == 0) Cache.Drafts.Remove(guid); else Cache.Drafts[guid] = text; } QueueSave(); }
    public Api Connection => api ?? throw new IOException("Connect to your server first.");
    public async Task RefreshPhotosAsync() { contactsAt = 0; groupsAt = 0; if (api is { } c) await Task.WhenAll(ContactsAsync(c, lifetime.Token), GroupsAsync(c, lifetime.Token)); }
    private async Task ContactsAsync(Api connection, CancellationToken token)
    {
        if (!await contactGate.WaitAsync(0, token)) return;
        contactsAt = Environment.TickCount64;
        try
        {
            JsonElement contacts;
            var photos = true;
            try { contacts = await connection.RequestAsync("/contact", query: new() { ["extraProperties"] = "avatar" }, token: token); }
            catch (IOException) { photos = false; contacts = await connection.RequestAsync("/contact", token: token); }
            var names = new Dictionary<string, string>(); var portraits = new Dictionary<string, string>();
            var decoded = new List<(JsonElement contact, string image, string identity)>(); var identities = new Dictionary<string, string>();
            var supplied = 0; var loaded = 0;
            foreach (var c in contacts.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var image = "";
                foreach (var field in new[] { "avatar", "contactImage", "contactThumbnailImage", "contactImageThumbnail" })
                {
                    if (!c.TryGetProperty(field, out var raw) || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(raw.GetString())) continue;
                    supplied++;
                    image = await photo(raw);
                    if (image.Length > 0) { loaded++; break; }
                }
                var id = Text(c, "id"); var identity = id.Length > 0 ? Text(c, "sourceType") + ":" + id : "";
                if (image.Length > 0 && identity.Length > 0) identities[identity] = image;
                decoded.Add((c.Clone(), image, identity));
            }
            foreach (var (c, own, identity) in decoded)
            {
                var name = Text(c, "displayName");
                if (name.Length == 0) name = (Text(c, "firstName") + " " + Text(c, "lastName")).Trim();
                var image = own.Length > 0 ? own : identities.GetValueOrDefault(identity) ?? "";
                foreach (var field in new[] { "phoneNumbers", "emails" })
                {
                    if (!c.TryGetProperty(field, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var address = entry.ValueKind == JsonValueKind.String ? entry.GetString()! : Text(entry, "address") is { Length: > 0 } a ? a : Text(entry, "value");
                        if (address.Length == 0) continue;
                        if (name.Length > 0) names[Model.AddressKey(address)] = name;
                        if (image.Length > 0) foreach (var key in Model.AddressKeys(address)) portraits[key] = image;
                    }
                }
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (connection != api) return;
                Cache.Contacts = names;
                if (photos) foreach (var p in portraits) Cache.ContactPhotos[p.Key] = p.Value;
            }
            log.Log("contacts", new { records = decoded.Count, supplied, loaded, failed = supplied - loaded, photos });
            Publish(); QueueSave();
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or HttpRequestException) { log.Log("contacts-failed", new { type = e.GetType().Name }); }
        finally { contactGate.Release(); }
    }
    private async Task GroupsAsync(Api connection, CancellationToken token)
    {
        groupsAt = Environment.TickCount64;
        List<Chat> groups;
        lock (gate) groups = Cache.Chats.Where(c => c.IsGroup).ToList();
        foreach (var group in groups)
        {
            try
            {
                var bytes = await connection.BytesAsync($"/chat/{Uri.EscapeDataString(group.Guid)}/icon", allowMissing: true, token: token);
                var path = bytes == null ? "" : await photo(JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes)));
                lock (gate) { token.ThrowIfCancellationRequested(); if (connection != api) return; if (bytes == null) Cache.GroupPhotos.Remove(group.Guid); else if (path.Length > 0) Cache.GroupPhotos[group.Guid] = path; }
                Publish(); QueueSave();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException) { log.Log("group-photo-failed", new { type = e.GetType().Name }); if (token.IsCancellationRequested) return; }
        }
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    private void Publish() => Changed?.Invoke();
    private void QueueSave()
    {
        lock (gate)
        {
            saveDelay?.Cancel(); saveDelay = new();
            _ = SaveLaterAsync(saveDelay.Token);
        }
    }
    private async Task SaveLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(750, token); await SaveAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Log("cache-save-failed"); }
    }
    public async Task SaveAsync()
    {
        string json;
        lock (gate)
        {
            Cache.Seen = Cache.Seen.TakeLast(10000).ToHashSet();
            var keep = Cache.Chats.OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).Take(100).Select(c => c.Guid).Append(Active ?? "").ToHashSet();
            foreach (var guid in Cache.Histories.Keys.Where(k => !keep.Contains(k)).ToList()) Cache.Histories.Remove(guid);
            json = Json.Write(Cache);
        }
        await store.SaveAsync("cache.json", Json.Read<ClientCache>(json)!);
    }
    public void Dispose() { lifetime.Cancel(); saveDelay?.Cancel(); api?.Dispose(); lifetime.Dispose(); }
}
