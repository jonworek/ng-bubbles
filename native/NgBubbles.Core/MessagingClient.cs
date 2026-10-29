using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace NgBubbles.Core;

public sealed class MessagingClient : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly SemaphoreSlim contactGate = new(1, 1);
    private readonly HashSet<string> reads = [];
    private readonly Dictionary<string, SemaphoreSlim> readGates = [];
    private readonly Dictionary<string, SemaphoreSlim> historyGates = [];
    private readonly HistoryPages historyPages;
    private readonly Dictionary<string, long> readChanges = [];
    private long revision;
    private readonly Dictionary<string, long> deletedThrough = [];
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
    public DateTimeOffset? ContactsRefreshedAt { get; private set; }
    public static TimeSpan ContactRefreshInterval => TimeSpan.FromHours(1);
    private long groupsAt;
    public ClientCache Cache { get; }
    public string? Active { get; private set; }
    public bool Focused { get; set; }
    public Func<ConnectionProfile, Task>? SaveConnectionProfileAsync { get; set; }
    public event Action? Changed;
    public event Action<IncomingNotification>? Notification;
    public MessagingClient(Store store, Diagnostics log, Func<JsonElement, Task<string>> photo)
    {
        this.store = store; this.log = log; this.photo = photo;
        historyPages = new(store);
        Cache = store.Load("cache.json", () => new ClientCache());
    }
    public Snapshot Snapshot()
    {
        lock (gate) return new(Conversations.List(Cache),
            Active == null ? [] : Conversations.Messages(Cache, Active), Active, Cache.Unread.Select(g => Conversations.Id(Cache, g)).ToHashSet(), new(Cache.Contacts),
            new(Cache.ContactPhotos), new(Cache.GroupPhotos), status, error.Length > 0 ? error : readError);
    }
    public async Task ConnectAsync(Preferences settings, string password)
    {
        await connectGate.WaitAsync();
        try
        {
            lifetime.Cancel(); lifetime.Dispose();
            lifetime = new();
            var token = lifetime.Token;
            preferences = settings;
            var connection = new Api(settings.Server, password, log, localServer: settings.LocalServer, computerId: settings.ServerComputerId);
            api?.Dispose(); api = connection;
            contactsAt = 0; groupsAt = 0;
            lock (gate) { status = "connecting"; error = ""; }
            Publish();
            live = new(connection.Server, password, log, () => connection.Server);
            _ = live.RunAsync((name, data) => LiveAsync(connection, name, data, token), () => { if (connection == api) RequestSync(); }, token);
            _ = PollAsync(connection, token);
            _ = SignalsAsync(connection, token);
            _ = ContactsAsync(connection, token);
            await SyncAsync(token);
        }
        finally { connectGate.Release(); }
    }
    private async Task PollAsync(Api connection, CancellationToken token)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20)); while (await timer.WaitForNextTickAsync(token)) { if (api != connection) return; RequestSync(); if (Environment.TickCount64 - contactsAt > ContactRefreshInterval.TotalMilliseconds) _ = ContactsAsync(connection, token); } }
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
            var previousServer = connection.Server;
            connection.UseSavedFallback(preferences);
            await connection.RefreshConnectionAsync(token: token);
            if (SaveConnectionProfileAsync != null) await SaveConnectionProfileAsync(connection.Profile);
            token.ThrowIfCancellationRequested();
            if (connection != api) return;
            if (connection.Server != previousServer) live?.Reconnect();
            long syncRevision;
            lock (gate) syncRevision = revision;
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
                var serverUnread = unread.SelectMany(m => m.Chats.Select(c => c.Guid).Concat(m.ChatGuid is { Length: > 0 } direct ? [direct] : []).Distinct().Select(guid => (Guid: guid, message: m))).ToList();
                var changed = readChanges.Where(p => p.Value > syncRevision).ToDictionary(p => p.Key, p => Cache.Unread.Contains(p.Key));
                Cache.Unread = serverUnread.Where(x => !Cache.ReadStates.TryGetValue(x.Guid, out var read) || !Covered(read, x.message)) .Select(x => x.Guid).ToHashSet();
                foreach (var item in changed) { if (item.Value) Cache.Unread.Add(item.Key); else Cache.Unread.Remove(item.Key); }
                foreach (var state in Cache.ReadStates.ToList())
                {
                    if (state.Value.ManualUnread) Cache.Unread.Add(state.Key);
                    var reflectedByServer = serverUnread.Any(x => x.Guid == state.Key);
                    if (!reads.Contains(state.Key) && (state.Value.ManualUnread && reflectedByServer || !state.Value.ManualUnread && !reflectedByServer)) { Cache.ReadStates.Remove(state.Key); log.Log("read-reconciled"); }
                }
                active = Active;
            }
            log.Log("unread-sync", new { serverMessages = unread.Count });
            foreach (var message in recovered) Accept(message, notify: true, updateUnread: false);
            lock (gate) Cache.Cursor = Math.Max(cursor, recovered.Select(m => m.DateCreated).DefaultIfEmpty(cursor).Max());
            if (active != null) await LoadConversationAsync(connection, active, null, token);
            List<string> pending;
            lock (gate) pending = Cache.ReadStates.Where(s => s.Value.LocalOnly && !s.Value.ManualUnread && !Cache.Unread.Contains(s.Key)).Select(s => s.Key).ToList();
            if (active != null && Focused && Active == active) await ReadAsync(active, token);
            foreach (var guid in pending) await ReadRawAsync(guid, token, false);
            lock (gate) { status = "connected"; error = live?.Connected == true ? "" : "Live connection reconnecting; checking every 20 seconds."; }
            Publish(); QueueSave();

            if (groupsAt == 0 || Environment.TickCount64 - groupsAt > 300000) _ = GroupsAsync(connection, token);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            if (token.IsCancellationRequested || connection != api) return;
            lock (gate) { status = e is JsonException ? "connected" : "offline"; error = e is JsonException ? "The server is reachable, but conversation data could not be read. Retrying automatically; see diagnostics." : e is IOException ? e.Message : "Connection interrupted. Retrying automatically."; }
            log.Log("sync-failed", new { type = e.GetType().Name }); Publish();
        }
        finally { syncGate.Release(); }
    }
    public void Accept(Message message, bool notify = true, bool updateUnread = true)
    {
        lock (gate)
        {
            if (message.Chats.Select(chat => chat.Guid).Append(message.ChatGuid ?? "").Any(guid => deletedThrough.TryGetValue(guid, out var through) && message.DateCreated <= through)) return;
        }
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
            if (chat == null) { chat = message.Chats.FirstOrDefault(c => c.Guid == guid) is { } supplied ? supplied with { LastMessage = null } : new Chat { Guid = guid }; Cache.Chats.Add(chat); }
            if (message.DateCreated >= (chat.LastMessage?.DateCreated ?? 0)) chat.LastMessage = message;
            if (updateUnread && !known && !message.IsFromMe && !message.IsRead && (!Cache.ReadStates.TryGetValue(guid, out var read) || !Covered(read, message))) { Cache.Unread.Add(guid); readChanges[guid] = ++revision; }
            if (notify && !known && !message.IsFromMe && !message.IsRead && message.DateCreated > Cache.Cursor - 3000 &&
                message.DateCreated >= DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds() && !(IsActive(guid) && Focused) && Cache.Unread.Contains(guid))
                notification = new(Conversations.Id(Cache, guid), Model.Title(chat, Cache.Contacts), message.Preview);
        }
        if (notification != null) Notification?.Invoke(notification);
        QueueSave(); Publish();
    }
    public void ApplyUpdate(JsonElement patch)
    {
        if (!patch.TryGetProperty("guid", out var id) || id.GetString() is not { } guid) return;
        Message? previous;
        lock (gate) previous = Cache.Histories.Values.SelectMany(h => h).FirstOrDefault(m => m.Guid == guid) ?? Cache.Chats.Select(c => c.LastMessage).FirstOrDefault(m => m?.Guid == guid);
        var merged = previous == null ? new JsonObject() : JsonSerializer.SerializeToNode(previous, Json.Options)!.AsObject();
        foreach (var property in patch.EnumerateObject()) merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        if (merged.Deserialize<Message>(Json.Options) is { } message) Accept(message, false, false);
    }
    private async Task LiveAsync(Api connection, string name, JsonElement data, CancellationToken token)
    {
        if (connection != api || token.IsCancellationRequested) return;
        try
        {
            if (name is "new-message" or "updated-message")
            {
                if (name == "updated-message") ApplyUpdate(data);
                else if (data.Deserialize<Message>(Json.Options) is { } message) Accept(message);
                if (Active is { } guid && Focused) await ReadAsync(guid, token);
            }
            else if (name == "chat-read-status-changed" && data.TryGetProperty("chatGuid", out var guid) && data.TryGetProperty("read", out var read))
            {
                lock (gate)
                {
                    if (read.ValueKind == JsonValueKind.True)
                    {
                        var chatGuid = guid.GetString()!;
                        if (Cache.ReadStates.GetValueOrDefault(chatGuid) is not { LocalOnly: true } pending)
                        { Cache.Unread.Remove(chatGuid); SetReadState(chatGuid, false); }
                        else if (!(Cache.Histories.GetValueOrDefault(chatGuid) ?? []).Any(m => !m.IsFromMe && !Covered(pending, m))) Cache.Unread.Remove(chatGuid);
                    }
                    else if (read.ValueKind == JsonValueKind.False) { SetReadState(guid.GetString()!, false).ManualUnread = true; Cache.Unread.Add(guid.GetString()!); }
                }
                Publish(); QueueSave();
            }
            if (name is "group-icon-changed" or "group-icon-removed") { groupsAt = 0; }
            RequestSync();
        }
        catch (Exception e) when (e is JsonException or IOException or OperationCanceledException) { log.Log("live-event-failed", new { type = e.GetType().Name }); }
    }
    public async Task<ConversationDeletionResult> DeleteConversationsAsync(IEnumerable<string> guids, CancellationToken token = default, IProgress<ConversationDeletionProgress>? progress = null)
    {
        var connection = api ?? throw new IOException("Connect to your Mac before deleting conversations.");
        var connectionToken = lifetime.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await syncGate.WaitAsync(token);
        var deleted = new List<string>();
        var failed = new List<string>();
        try
        {
            List<(string Id, List<string> Sources)> targets;
            lock (gate)
            {
                var known = Conversations.List(Cache).Select(chat => chat.Guid).ToHashSet();
                targets = guids.Distinct().Select(guid => known.Contains(guid)
                    ? (guid, Conversations.Sources(Cache, guid).Select(chat => chat.Guid).ToList())
                    : throw new ArgumentException("A selected conversation is no longer available. Refresh the list and try again.")).ToList();
            }
            if (targets.Count == 0) return new(deleted, failed);
            var info = await connection.RequestAsync("/server/info", token: token);
            if (!info.TryGetProperty("private_api", out var enabled) || enabled.ValueKind != JsonValueKind.True)
                throw new IOException("Enable Private API in BlueBubbles Server to delete conversations on your Mac.");
            progress?.Report(new(targets.Count, 0, 0, 0));
            foreach (var target in targets)
            {
                if (token.IsCancellationRequested) break;
                var complete = true;
                foreach (var guid in target.Sources)
                {
                    if (token.IsCancellationRequested) { complete = false; continue; }
                    try { await connection.RequestAsync("/chat/" + Uri.EscapeDataString(guid), token: connectionToken, method: HttpMethod.Delete); }
                    catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException)
                    {
                        complete = false;
                        log.Log("conversation-delete-unconfirmed", new { type = e.GetType().Name });
                        continue;
                    }
                    SemaphoreSlim historyGate;
                    lock (gate) if (!historyGates.TryGetValue(guid, out historyGate!)) historyGates[guid] = historyGate = new(1, 1);
                    await historyGate.WaitAsync();
                    try
                    {
                        lock (gate)
                        {
                            deletedThrough[guid] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            Cache.Chats.RemoveAll(chat => chat.Guid == guid);
                            Cache.Histories.Remove(guid); Cache.HistoryStarts.Remove(guid);
                            Cache.Unread.Remove(guid); Cache.ReadStates.Remove(guid); Cache.GroupPhotos.Remove(guid);
                        }
                        try { await historyPages.DeleteAsync(guid); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Log("deleted-history-cleanup-failed", new { type = e.GetType().Name }); }
                    }
                    finally { historyGate.Release(); }
                }
                lock (gate)
                {
                    if (complete)
                    {
                        deleted.Add(target.Id);
                        Cache.Drafts.Remove(target.Id);
                        foreach (var source in target.Sources) Cache.Drafts.Remove(source);
                        Cache.Unread.Remove(target.Id); Cache.ReadStates.Remove(target.Id);
                        foreach (var mapping in Cache.ConversationIds.Where(pair => pair.Value == target.Id).Select(pair => pair.Key).ToList()) Cache.ConversationIds.Remove(mapping);
                        if (Active == target.Id) Active = null;
                    }
                    else failed.Add(target.Id);
                }
                Publish();
                progress?.Report(new(targets.Count, deleted.Count + failed.Count, deleted.Count, failed.Count));
            }
            Publish(); await SaveAsync();
            log.Log("conversations-deleted", new { confirmed = deleted.Count, unconfirmed = failed.Count });
            return new(deleted, failed);
        }
        finally { syncGate.Release(); }
    }
    public void Home()
    {
        lock (gate) Active = null;
        Publish();
    }
    public void Select(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid) || guid.Length > 512) throw new ArgumentException("Select a valid conversation.");
        lock (gate) Active = Conversations.Id(Cache, guid);
        Publish();
    }
    public async Task OpenAsync(string guid, long? before = null)
    {
        if (before == null) Select(guid);
        else Publish();
        var connection = api;
        try
        {
            await LoadConversationAsync(connection, guid, before, lifetime.Token);
            if (before == null && Focused && IsActive(guid)) await ReadAsync(guid, force: true);
        }
        finally { Publish(); QueueSave(); }
    }
    private bool IsActive(string guid) { lock (gate) return Active != null && Conversations.Id(Cache, guid) == Active; }
    public bool CanLoadOlder(string guid)
    {
        lock (gate) return Conversations.Sources(Cache, guid).Any(c => Conversations.HasOlder(Cache, c.Guid));
    }
    private async Task LoadConversationAsync(Api? connection, string guid, long? before, CancellationToken token)
    {
        List<string> sources;
        lock (gate) sources = Conversations.Sources(Cache, guid).Select(c => c.Guid).ToList();
        foreach (var source in sources)
        {
            long? cursor = null;
            if (before != null)
            {
                lock (gate)
                {
                    var history = Cache.Histories.GetValueOrDefault(source);
                    if (history is not { Count: > 0 }) continue;
                    if (!Conversations.HasOlder(Cache, source) || history[0].DateCreated < before) continue;
                    cursor = history[0].DateCreated;
                }
            }
            await LoadHistoryAsync(connection, source, cursor, token);
        }
    }
    private async Task LoadHistoryAsync(Api? connection, string guid, long? before, CancellationToken token)
    {
        SemaphoreSlim historyGate;
        lock (gate)
        {
            if (!historyGates.TryGetValue(guid, out historyGate!)) historyGates[guid] = historyGate = new(1, 1);
        }
        await historyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool empty;
            lock (gate) empty = !Cache.Histories.TryGetValue(guid, out var history) || history.Count == 0;
            if (before == null && empty && await historyPages.LatestAsync(guid).ConfigureAwait(false) is { } latest)
            {
                lock (gate)
                {
                    Cache.Histories[guid] = Model.Merge(latest.Messages, Cache.Histories.GetValueOrDefault(guid) ?? []);
                    if (latest.Start is { } start) Cache.HistoryStarts[guid] = start;
                }
                Publish();
            }
            if (before != null)
            {
                lock (gate)
                {
                    if (Cache.Histories.TryGetValue(guid, out var existing) && existing.Count > 0 && existing[0].DateCreated < before ||
                        Cache.HistoryStarts.TryGetValue(guid, out var start) && before <= start) return;
                }
            }
            HistoryPage? page = null;
            if (before != null)
            {
                try { page = await historyPages.BeforeAsync(guid, before.Value).ConfigureAwait(false); }
                catch (IOException) { log.Log("history-cache-read-failed"); }
            }
            var cached = page != null;
            if (page == null)
            {
                if (connection == null) return;
                var json = await connection.RequestAsync($"/chat/{Uri.EscapeDataString(guid)}/message", query: new() { ["with"] = "attachments,handle", ["sort"] = "DESC", ["limit"] = "100", ["before"] = before?.ToString() }, token: token).ConfigureAwait(false);
                var messages = Model.Merge([], json.Deserialize<List<Message>>(Json.Options) ?? []);
                page = new(messages, messages.Count < 100 ? messages.Select(message => message.DateCreated).DefaultIfEmpty(before ?? 0).Min() : null);
                try { await historyPages.SaveAsync(guid, before, page).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Log("history-cache-save-failed"); }
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                var existing = Cache.Histories.GetValueOrDefault(guid) ?? [];
                Cache.Histories[guid] = cached ? Model.Merge(page.Messages, existing) : Model.Merge(existing, page.Messages);
                if (page.Start is { } start) Cache.HistoryStarts[guid] = start;
            }
            log.Log("history-page", new { cached, messages = page.Messages.Count, complete = page.Start != null });
        }
        finally { historyGate.Release(); }
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
        readChanges[guid] = ++revision;
        return state;
    }
    public async Task ReadAsync(string guid, CancellationToken token = default, bool force = false)
    {
        List<string> sources;
        lock (gate) sources = Conversations.Sources(Cache, guid).Select(c => c.Guid).ToList();
        foreach (var source in sources) await ReadRawAsync(source, token, force);
    }
    private async Task ReadRawAsync(string guid, CancellationToken token, bool force)
    {
        SemaphoreSlim readGate;
        lock (gate)
        {
            if (!readGates.TryGetValue(guid, out readGate!)) readGates[guid] = readGate = new(1, 1);
        }
        await readGate.WaitAsync(token);
        try { await ReadCoreAsync(guid, token, force); }
        finally { readGate.Release(); }
    }
    private async Task ReadCoreAsync(string guid, CancellationToken token, bool force)
    {
        ReadState? state;
        Api? connection;
        lock (gate)
        {
            if (force && (!IsActive(guid) || !Focused)) return;
            if (Cache.Unread.Remove(guid) || force) SetReadState(guid, true);
            state = Cache.ReadStates.GetValueOrDefault(guid);
            connection = api;
            if (state is not { LocalOnly: true } || connection == null ||
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.SentAt < 5000 || !reads.Add(guid)) return;
        }
        Publish(); QueueSave();
        try
        {
            if (!force && !(IsActive(guid) && Focused))
            {
                await LoadHistoryAsync(connection, guid, null, token);
                lock (gate)
                {
                    if ((Cache.Histories.GetValueOrDefault(guid) ?? []).Any(m => !m.IsFromMe && !m.IsRead && !Covered(state, m)))
                    { Cache.Unread.Add(guid); readChanges[guid] = ++revision; QueueSave(); return; }
                }
            }
            var info = await connection.RequestAsync("/server/info", token: token);
            if (!info.TryGetProperty("private_api", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            { lock (gate) readError = "Read locally. Enable Private API on the Mac to synchronize read status."; log.Log("read-unavailable"); return; }
            lock (gate) state.SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.RequestAsync($"/chat/{Uri.EscapeDataString(guid)}/read", post: true, token: token);
            lock (gate)
            {
                if (connection != api) return;
                if (ReferenceEquals(Cache.ReadStates.GetValueOrDefault(guid), state)) { state.SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                readError = "";
            }
            log.Log("read-sent", new { awaitingServerSync = true }); RequestSync(); QueueSave();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        {
            lock (gate) readError = "Read locally. Server read sync failed; retrying automatically.";
            log.Log("read-retry");
            _ = RetryReadsAsync(lifetime.Token);
        }
        finally { lock (gate) reads.Remove(guid); Publish(); }
    }
    private async Task RetryReadsAsync(CancellationToken token)
    {
        try { await Task.Delay(5100, token); RequestSync(); }
        catch (OperationCanceledException) { }
    }
    public async Task SendAsync(string guid, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) throw new IOException("Enter a message up to 20,000 characters.");
        var connection = api ?? throw new IOException("Connect to your server first.");
        lock (gate) guid = Conversations.SendGuid(Cache, guid);
        var result = await connection.RequestAsync("/message/text", new { chatGuid = guid, message = text, tempGuid = Guid.NewGuid().ToString(), method = preferences.SendMethod }, token: lifetime.Token);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("guid", out _))
        {
            var message = result.Deserialize<Message>(Json.Options)!;
            Accept(message with { IsFromMe = true, ChatGuid = message.ChatGuid ?? message.Chats.FirstOrDefault()?.Guid ?? guid }, false);
            if (message.Error is not (null or 0)) throw new IOException("The server reported that this message was not sent. Your draft has been kept.");
        }
        RequestSync();
    }
    public async Task<Message> LinkDetailsAsync(Message message)
    {
        if (message.PayloadData != null) return message;
        var details = await Connection.RequestAsync($"/message/{Uri.EscapeDataString(message.Guid)}", query: new() { ["with"] = "payloadData,attachments" }, token: lifetime.Token);
        var loaded = details.Deserialize<Message>(Json.Options);
        if (loaded?.PayloadData == null) return message;
        var enriched = message with { PayloadData = loaded.PayloadData, BalloonBundleId = loaded.BalloonBundleId ?? message.BalloonBundleId };
        Accept(enriched, false, false);
        return enriched;
    }
    public async Task<string> CreateChatAsync(string[] addresses, string text, string service)
    {
        if (addresses.Length is < 1 or > 32 || addresses.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 254) || string.IsNullOrWhiteSpace(text) || text.Length > 20000 || (service != "iMessage" && service != "SMS")) throw new IOException("Enter recipients and an initial message.");
        var connection = api ?? throw new IOException("Connect first.");
        var result = await connection.RequestAsync("/chat/new", new { addresses, message = text, service, method = preferences.SendMethod }, token: lifetime.Token);
        await SyncAsync(lifetime.Token);
        return result.GetProperty("guid").GetString()!;
    }
    public Dictionary<string, string> Drafts()
    {
        lock (gate)
        {
            var drafts = new Dictionary<string, string>();
            foreach (var (guid, text) in Cache.Drafts)
            {
                var id = Conversations.Id(Cache, guid);
                if (!drafts.ContainsKey(id) || guid == id) drafts[id] = text;
            }
            return drafts;
        }
    }
    public string Draft(string guid)
    {
        lock (gate) return Cache.Drafts.GetValueOrDefault(Conversations.Id(Cache, guid)) ??
            Conversations.Sources(Cache, guid).Select(c => Cache.Drafts.GetValueOrDefault(c.Guid)).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
    }
    public void Draft(string guid, string text) { lock (gate) { guid = Conversations.Id(Cache, guid); if (text.Length == 0) { foreach (var source in Conversations.Sources(Cache, guid)) Cache.Drafts.Remove(source.Guid); Cache.Drafts.Remove(guid); } else Cache.Drafts[guid] = text; } QueueSave(); }
    public Api Connection => api ?? throw new IOException("Connect to your server first.");
    public async Task<int> RefreshContactsAsync()
    {
        var connection = api ?? throw new IOException("Connect to your Mac before refreshing contacts.");
        return await ContactsAsync(connection, lifetime.Token, true);
    }
    public async Task RefreshPhotosAsync()
    {
        await RefreshContactsAsync();
        if (api is { } connection) await GroupsAsync(connection, lifetime.Token);
    }
    private async Task<int> ContactsAsync(Api connection, CancellationToken token, bool manual = false)
    {
        if (manual) await contactGate.WaitAsync(token);
        else if (!await contactGate.WaitAsync(0, token)) return -1;
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
                if (connection != api) return -1;
                Cache.Contacts = names; ContactsRefreshedAt = DateTimeOffset.UtcNow;
                if (photos) foreach (var p in portraits) Cache.ContactPhotos[p.Key] = p.Value;
            }
            log.Log("contacts", new { records = decoded.Count, supplied, loaded, failed = supplied - loaded, photos });
            Publish(); QueueSave();
            return names.Count;
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or HttpRequestException)
        {
            log.Log("contacts-failed", new { type = e.GetType().Name });
            contactsAt = Environment.TickCount64 - (long)ContactRefreshInterval.TotalMilliseconds + 300000;
            if (manual) throw new IOException("Contact refresh failed. Your saved names and photos have been kept. Try again when your Mac is reachable.", e);
            return -1;
        }
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
            catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException) { log.Log("group-photo-failed", new { type = e.GetType().Name }); return; }
        }
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    private void Publish() => Changed?.Invoke();
    private void QueueSave()
    {
        lock (gate)
        {
            saveDelay?.Cancel(); saveDelay?.Dispose(); saveDelay = new();
            var token = saveDelay.Token;
            _ = Task.Run(() => SaveLaterAsync(token));
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
            var keep = Cache.Chats.OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).Take(100).Select(c => c.Guid).Concat(Active == null ? [] : Conversations.Sources(Cache, Active).Select(c => c.Guid)).ToHashSet();
            foreach (var guid in Cache.Histories.Keys.Where(k => !keep.Contains(k)).ToList()) Cache.Histories.Remove(guid);
            json = Json.Write(new ClientCache
            {
                Chats = Cache.Chats, Histories = Cache.Histories.ToDictionary(pair => pair.Key, pair => pair.Value.TakeLast(1000).ToList()), HistoryStarts = Cache.HistoryStarts, ConversationIds = Cache.ConversationIds,
                Contacts = Cache.Contacts, ContactPhotos = Cache.ContactPhotos, GroupPhotos = Cache.GroupPhotos, Unread = Cache.Unread,
                ReadStates = Cache.ReadStates, Seen = Cache.Seen, Cursor = Cache.Cursor, Drafts = Cache.Drafts
            });
        }
        await store.SaveAsync("cache.json", Json.Read<ClientCache>(json)!);
    }
    public void Dispose() { lifetime.Cancel(); saveDelay?.Cancel(); api?.Dispose(); lifetime.Dispose(); }
}
