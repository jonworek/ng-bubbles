using System.Text.RegularExpressions;

namespace NgBubbles.Core;

public sealed record MessageLink(int Start, int Length, string Url);
public static partial class MessageLinks
{
    public static List<MessageLink> Find(string text)
    {
        var links = new List<MessageLink>();
        foreach (Match match in WebLinks().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
                while (value.EndsWith(close) && value.Count(c => c == close) > value.Count(c => c == open)) value = value[..^1];
            var candidate = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0)
                links.Add(new(match.Index, value.Length, uri.AbsoluteUri));
        }
        return links;
    }
    [GeneratedRegex("(?<![\\p{L}\\p{N}_@])(?:https?://|www\\.)[^\\s\\p{C}<>\\\"'“”‘’`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex WebLinks();
}
