using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NgBubbles.Core;

public sealed record MessageReaction(string Emoji, string Sender, bool IsFromMe, string EventGuid);
public static partial class MessageReactions
{
    private sealed record ReactionEvent(string? Target, string? QuotedText, string Emoji, bool Remove);
    public static List<Message> Apply(IReadOnlyList<Message> messages)
    {
        var events = messages.Select(message => (Message: message, Event: Read(message))).Where(item => item.Event != null).ToList();
        if (events.Count == 0) return messages.ToList();
        var eventIds = events.Select(item => item.Message.Guid).ToHashSet();
        var originals = messages.Where(message => !eventIds.Contains(message.Guid)).ToList();
        var byId = originals.GroupBy(message => message.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var attached = new HashSet<string>();
        var reactions = new Dictionary<string, Dictionary<string, MessageReaction>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (message, value) in events.OrderBy(item => item.Message.DateCreated))
        {
            var reaction = value!;
            Message? target = null;
            if (reaction.Target != null) byId.TryGetValue(reaction.Target, out target);
            else if (reaction.QuotedText != null)
            {
                var matches = originals.Where(original => original.DateCreated <= message.DateCreated &&
                    original.Text?.Replace("\uFFFC", "").Trim() == reaction.QuotedText).Take(2).ToList();
                if (matches.Count == 1) target = matches[0];
            }
            if (target == null) continue;
            var address = message.Handle?.Address ?? "";
            var sender = message.IsFromMe ? "self" : address.Length > 0 ? string.Join('|', Model.AddressKeys(address).Order(StringComparer.Ordinal)) : "unknown:" + message.Guid;
            if (!reactions.TryGetValue(target.Guid, out var members)) reactions[target.Guid] = members = new(StringComparer.OrdinalIgnoreCase);
            if (!reaction.Remove) members[sender] = new(reaction.Emoji, address, message.IsFromMe, message.Guid);
            else if (members.TryGetValue(sender, out var previous) && previous.Emoji == reaction.Emoji) members.Remove(sender);
            attached.Add(message.Guid);
        }
        return messages.Where(message => !attached.Contains(message.Guid)).Select(message => message with
        {
            Reactions = reactions.TryGetValue(message.Guid, out var members) ? members.Values.ToList() : []
        }).ToList();
    }
    private static ReactionEvent? Read(Message message)
    {
        var text = TextReaction().Match(message.Text ?? "");
        var type = message.AssociatedMessageType;
        if (type is >= 2000 and <= 2006 or >= 3000 and <= 3006)
        {
            var emoji = (type % 1000) switch { 0 => "❤️", 1 => "👍", 2 => "👎", 3 => "😂", 4 => "‼️", 5 => "❓", 6 => message.AssociatedMessageEmoji ?? (text.Success ? TextEmoji(text.Groups["verb"].Value) : null), _ => null };
            if (!ValidEmoji(emoji)) return null;
            var target = message.AssociatedMessageGuid?.Trim();
            if (target?.StartsWith("p:", StringComparison.Ordinal) == true || target?.StartsWith("bp:", StringComparison.Ordinal) == true)
                target = target.Contains('/') ? target[(target.IndexOf('/') + 1)..] : target.StartsWith("bp:", StringComparison.Ordinal) ? target[3..] : null;
            return new(string.IsNullOrWhiteSpace(target) ? null : target, text.Success ? text.Groups["body"].Value.Trim() : null, emoji!, type >= 3000);
        }
        if (type is not (null or 0) || !text.Success) return null;
        var symbol = TextEmoji(text.Groups["verb"].Value);
        return !ValidEmoji(symbol) ? null : new(null, text.Groups["body"].Value.Trim(), symbol!, text.Groups["verb"].Value.StartsWith("Removed", StringComparison.Ordinal));
    }
    private static bool ValidEmoji(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        StringInfo.ParseCombiningCharacters(value).Length == 1 && value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol || rune.Value is 0x20e3 or 0x203c or 0x2049);
    private static string? TextEmoji(string verb) => verb switch
    {
        "Loved" or "Removed a heart from" => "❤️", "Liked" or "Removed a like from" => "👍",
        "Disliked" or "Removed a dislike from" => "👎", "Laughed at" or "Removed a laugh from" => "😂",
        "Emphasized" or "Removed an emphasis from" => "‼️", "Questioned" or "Removed a question mark from" => "❓",
        _ => verb.StartsWith("Reacted ", StringComparison.Ordinal) && verb.EndsWith(" to", StringComparison.Ordinal) ? verb[8..^3] :
            verb.StartsWith("Removed ", StringComparison.Ordinal) && verb.EndsWith(" from", StringComparison.Ordinal) ? verb[8..^5] :
            verb.EndsWith(" to", StringComparison.Ordinal) ? verb[..^3] : null
    };
    [GeneratedRegex("^(?<verb>Loved|Liked|Disliked|Laughed at|Emphasized|Questioned|Reacted .{1,64}? to|Removed (?:a heart|a like|a dislike|a laugh|an emphasis|a question mark) from|Removed .{1,64}? from|.{1,64}? to) [“\\\"](?<body>.+)[”\\\"]$", RegexOptions.Singleline | RegexOptions.CultureInvariant, 250)]
    private static partial Regex TextReaction();
}
