using System.Text.Json;
using System.Text.RegularExpressions;

namespace NgBubbles.Core;

public sealed record RichLink(string Url, string Title, string Site, int? ImageIndex);
public static partial class RichLinks
{
    public static bool IsPlugin(Attachment attachment) => attachment.TransferName.EndsWith(".pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase);
    public static RichLink? Read(Message message)
    {
        var url = UrlPattern().Matches(message.Text ?? "").Select(match => SafeUrl(match.Value.TrimEnd('.', ',', ')', ']'))).FirstOrDefault(value => value != null);
        if (!(message.BalloonBundleId?.Contains("URLBalloonProvider", StringComparison.OrdinalIgnoreCase) == true || message.Attachments.Any(IsPlugin))) return null;
        var title = ""; int? index = null;
        if (message.PayloadData is { ValueKind: JsonValueKind.Array } payload)
        {
            foreach (var archive in payload.EnumerateArray().Take(4))
            {
                if (archive.ValueKind != JsonValueKind.Object || !archive.TryGetProperty("$objects", out var objects) || objects.ValueKind != JsonValueKind.Array || objects.GetArrayLength() > 4096) continue;
                var metadata = objects.EnumerateArray().FirstOrDefault(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("richLinkMetadata", out _));
                if (metadata.ValueKind == JsonValueKind.Object) metadata = Field(metadata, "richLinkMetadata", objects);
                if (metadata.ValueKind != JsonValueKind.Object) continue;
                title = Text(Field(metadata, "title", objects));
                var link = Field(metadata, "originalURL", objects);
                var original = SafeUrl(Text(Field(link, "NS.relative", objects)));
                var resolved = Field(metadata, "URL", objects);
                url ??= original ?? SafeUrl(Text(Field(resolved, "NS.relative", objects)));
                var image = Field(metadata, "image", objects);
                var substitute = Field(image, "richLinkImageAttachmentSubstituteIndex", objects);
                if (substitute.ValueKind == JsonValueKind.Number && substitute.TryGetInt32(out var value) && value >= 0 && value < message.Attachments.Count) index = value;
                break;
            }
        }
        if (url == null) return message.BalloonBundleId?.Contains("URLBalloonProvider", StringComparison.OrdinalIgnoreCase) == true ? new("", "Shared link", "", null) : null;
        var uri = new Uri(url);
        return new(url, string.IsNullOrWhiteSpace(title) ? uri.Host : title[..Math.Min(title.Length, 500)], uri.Host, index);
    }
    private static JsonElement Field(JsonElement value, string name, JsonElement objects)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return default;
        var visited = new HashSet<int>();
        while (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("UID", out var uid) && uid.ValueKind == JsonValueKind.Number && uid.TryGetInt32(out var index))
        {
            if (index < 0 || index >= objects.GetArrayLength() || !visited.Add(index) || visited.Count > 16) return default;
            value = objects[index];
        }
        return value;
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public static string? SafeUrl(string? value) => value is { Length: <= 4096 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http") && uri.UserInfo.Length == 0 && uri.Host.Length > 0 ? uri.AbsoluteUri : null;
    [GeneratedRegex("https?://[^\\s<>\\\"]+", RegexOptions.IgnoreCase)] private static partial Regex UrlPattern();
}
