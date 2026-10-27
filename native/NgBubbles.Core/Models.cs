using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NgBubbles.Core;

public static class AppInfo
{
    public const string Name = "NG Bubbles";
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.4.15";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static T? Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed record Participant(string Address = "");
public sealed record Attachment
{
    public string Guid { get; init; } = "";
    private string name = "Attachment";
    private string mime = "";
    public string TransferName { get => name; init => name = value ?? "Attachment"; }
    public string MimeType { get => mime; init => mime = value ?? ""; }
    public long TotalBytes { get; init; }
}
public sealed record Message
{
    public string Guid { get; init; } = "";
    public string? Text { get; init; }
    public long DateCreated { get; init; }
    public long? DateDelivered { get; init; }
    public long? DateRead { get; init; }
    public bool IsFromMe { get; init; }
    public bool IsRead { get; init; }
    public string? Service { get; init; }
    public string? BalloonBundleId { get; init; }
    public JsonElement? PayloadData { get; init; }
    public int? Error { get; init; }
    public string? ChatGuid { get; init; }
    public List<Chat> Chats { get; init; } = [];
    public Participant? Handle { get; init; }
    public List<Attachment> Attachments { get; init; } = [];
    public string Preview => !string.IsNullOrWhiteSpace(Text?.Replace("\uFFFC", "")) ? Text.Replace("\uFFFC", "") : Attachments.Count > 0 ? "Attachment" : "Message";
}
public sealed record Chat
{
    public string Guid { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? ServiceName { get; init; }
    public int Style { get; init; }
    public List<Participant> Participants { get; init; } = [];
    public Message? LastMessage { get; set; }
    public bool IsGroup => Style == 43 || Participants.Count > 1 || Guid.Contains(";+;");
}
public sealed class Preferences
{
    private string server = "";
    public string Server { get => server; set => server = value ?? ""; }
    private string localServer = "";
    public string LocalServer { get => localServer; set => localServer = value ?? ""; }
    private string secret = "";
    public string Secret { get => secret; set => secret = value ?? ""; }
    private string sendMethod = "apple-script";
    public string SendMethod { get => sendMethod; set => sendMethod = value ?? "apple-script"; }
    public bool Notifications { get; set; } = true;
    public bool Preview { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    private string migrationNotice = "";
    public string MigrationNotice { get => migrationNotice; set => migrationNotice = value ?? ""; }
}
public sealed class ReadState
{
    public long Through { get; set; }
    public HashSet<string> Guids { get; set; } = [];
    public bool LocalOnly { get; set; }
    public bool ManualUnread { get; set; }
    public long ConfirmedAt { get; set; }
    public long SentAt { get; set; }
}
public sealed class ClientCache
{
    public List<Chat> Chats { get; set; } = [];
    public Dictionary<string, List<Message>> Histories { get; set; } = [];
    public Dictionary<string, long> HistoryStarts { get; set; } = [];
    public Dictionary<string, string> ConversationIds { get; set; } = [];
    public Dictionary<string, string> Contacts { get; set; } = [];
    public Dictionary<string, string> ContactPhotos { get; set; } = [];
    public Dictionary<string, string> GroupPhotos { get; set; } = [];
    public HashSet<string> Unread { get; set; } = [];
    public Dictionary<string, ReadState> ReadStates { get; set; } = [];
    public HashSet<string> Seen { get; set; } = [];
    public long Cursor { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Dictionary<string, string> Drafts { get; set; } = [];
}
public sealed record IncomingNotification(string ChatGuid, string Title, string Body);
public sealed record Snapshot(List<Chat> Chats, List<Message> Messages, string? Active, HashSet<string> Unread,
    Dictionary<string, string> Contacts, Dictionary<string, string> ContactPhotos, Dictionary<string, string> GroupPhotos, string Status, string Error);

public static partial class Model
{
    public static string NormalizeServer(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath != "/api/v1" && uri.AbsolutePath != "/api/v1/"))
            throw new ArgumentException("Enter your server root URL, such as https://your-mac.example.com.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static string[] AddressKeys(string address)
    {
        var value = address.Trim().ToLowerInvariant().Replace("mailto:", "");
        if (value.Contains('@')) return ["email:" + value];
        var digits = Digits().Replace(Extension().Replace(value, ""), "");
        if (digits.Length == 0) return [];
        if (digits.Length == 11 && digits.StartsWith('1')) return ["phone:" + digits, "phone:" + digits[1..]];
        if (digits.Length == 10 && !value.StartsWith('+')) return ["phone:" + digits, "phone:1" + digits];
        return ["phone:" + digits];
    }
    public static string AddressKey(string address) => address.Contains('@') ? address.ToLowerInvariant() : Digits().Replace(address, "") is var d && d.Length > 10 ? d[^10..] : Digits().Replace(address, "");
    public static string Name(string address, IReadOnlyDictionary<string, string> contacts) => contacts.GetValueOrDefault(AddressKey(address)) ?? address;
    public static string Title(Chat chat, IReadOnlyDictionary<string, string> contacts) => !string.IsNullOrWhiteSpace(chat.DisplayName) ? chat.DisplayName : chat.Participants.Count > 0 ? string.Join(", ", chat.Participants.Select(p => Name(p.Address, contacts))) : "Conversation";
    public static string Photo(string address, IReadOnlyDictionary<string, string> photos) => AddressKeys(address).Select(k => photos.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    public static List<Message> Merge(IEnumerable<Message> old, IEnumerable<Message> incoming) => old.Concat(incoming).Where(m => m.Guid.Length > 0).GroupBy(m => m.Guid).Select(g => g.Last() with { PayloadData = g.Last().PayloadData ?? g.LastOrDefault(m => m.PayloadData != null)?.PayloadData }).OrderBy(m => m.DateCreated).ThenBy(m => m.Guid, StringComparer.Ordinal).ToList();
    public static string MessageService(Message message, Chat? chat = null)
    {
        var services = new[] { message.Service, message.ChatGuid?.Split(';')[0] }
            .Concat(message.Chats.SelectMany(c => new[] { c.ServiceName, c.Guid.Split(';')[0] }))
            .Concat(new[] { chat?.ServiceName, chat?.Guid.Split(';')[0] });
        return services.FirstOrDefault(s => s is not null && (s.Equals("iMessage", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("SMS", StringComparison.OrdinalIgnoreCase) || s.Equals("MMS", StringComparison.OrdinalIgnoreCase) || s.Equals("RCS", StringComparison.OrdinalIgnoreCase))) ?? "";
    }
    public static bool IsTextMessage(Message message) => MessageService(message) is var service &&
        (service.Equals("SMS", StringComparison.OrdinalIgnoreCase) || service.Equals("MMS", StringComparison.OrdinalIgnoreCase) || service.Equals("RCS", StringComparison.OrdinalIgnoreCase));
    [GeneratedRegex(@"\D")] private static partial Regex Digits();
    [GeneratedRegex(@"(?:ext\.?|extension|x|;ext=)\s*\d+$")] private static partial Regex Extension();
}
