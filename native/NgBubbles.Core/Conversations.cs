namespace NgBubbles.Core;

internal static class Conversations
{
    private static string Key(Chat chat)
    {
        if (chat.IsGroup) return "chat:" + chat.Guid;
        var parts = chat.Guid.Split(';', 3);
        var address = parts.Length == 3 && parts[1] == "-" ? parts[2] : chat.Participants.SingleOrDefault()?.Address;
        if (string.IsNullOrWhiteSpace(address)) return "chat:" + chat.Guid;
        return Model.AddressKeys(address).OrderByDescending(k => k.Length).FirstOrDefault() ?? "chat:" + chat.Guid;
    }
    public static string Id(ClientCache cache, string guid)
    {
        var chat = cache.Chats.FirstOrDefault(c => c.Guid == guid);
        if (chat == null) return guid;
        var key = Key(chat);
        if (!cache.ConversationIds.TryGetValue(key, out var id)) cache.ConversationIds[key] = id = guid;
        return id;
    }
    public static List<Chat> Sources(ClientCache cache, string guid)
    {
        var raw = cache.Chats.FirstOrDefault(c => c.Guid == guid);
        var key = raw != null ? Key(raw) : cache.ConversationIds.FirstOrDefault(p => p.Value == guid).Key;
        var sources = key == null ? [] : cache.Chats.Where(c => Key(c) == key).ToList();
        return sources.Count > 0 ? sources : [new Chat { Guid = guid }];
    }
    public static List<Chat> List(ClientCache cache) => cache.Chats.GroupBy(Key).Select(group =>
    {
        var first = group.First();
        var latest = group.OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).First();
        return first with { Guid = Id(cache, first.Guid), LastMessage = latest.LastMessage,
            DisplayName = group.Select(c => c.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) };
    }).OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).ToList();
    public static bool HasOlder(ClientCache cache, string guid) => cache.Histories.TryGetValue(guid, out var history) && history.Count > 0 &&
        (!cache.HistoryStarts.TryGetValue(guid, out var start) || history[0].DateCreated > start);
    public static List<Message> Messages(ClientCache cache, string guid)
    {
        var sources = Sources(cache, guid);
        var boundary = sources.Where(c => HasOlder(cache, c.Guid)).Select(c => cache.Histories[c.Guid][0].DateCreated).DefaultIfEmpty(0).Max();
        return MessageReactions.Apply(Model.Merge([], sources.SelectMany(chat => (cache.Histories.GetValueOrDefault(chat.Guid) ?? [])
            .Select(message => message with { ChatGuid = message.ChatGuid ?? chat.Guid, Service = Model.MessageService(message, chat) })))
            .Where(m => m.DateCreated >= boundary).ToList());
    }
    public static string SendGuid(ClientCache cache, string guid)
    {
        var sources = Sources(cache, guid);
        var latest = sources.SelectMany(chat => (cache.Histories.GetValueOrDefault(chat.Guid) ?? [])
            .Concat(chat.LastMessage is { } last ? [last] : []).Where(m => m.Error is null or 0).Select(m => (chat.Guid, m.DateCreated)))
            .OrderByDescending(m => m.DateCreated).FirstOrDefault();
        return latest.Guid ?? sources[0].Guid;
    }
}
